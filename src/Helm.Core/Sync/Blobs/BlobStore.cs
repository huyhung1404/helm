using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Core.Sync;

/// <summary>A blob's chunks are neither in the local cache nor on the server (e.g. it was never uploaded from its device).</summary>
public sealed class BlobUnavailableException(string message) : Exception(message);

/// <summary>A file is larger than the server accepts (<see cref="SyncLimits.MaxBlobBytes"/>).</summary>
public sealed class BlobTooLargeException(long size, long max)
    : Exception($"This file is {size / 1048576.0:0.#} MB; the sync server accepts files up to {max / 1048576.0:0.#} MB.");

/// <summary>What the engine needs from the blob store during a run.</summary>
public interface IBlobSync
{
    /// <summary>Uploads every pending blob that can be uploaded. Returns false when the quota stopped it.</summary>
    Task<bool> UploadPendingAsync(CancellationToken ct);

    /// <summary>True while the blob is only on this device; records that use it are not pushed yet.</summary>
    bool IsPending(string id);

    /// <summary>Deletes blobs no record uses (after a grace period) and restores used ones that were deleted.</summary>
    Task CollectGarbageAsync(IReadOnlySet<string> referenced, CancellationToken ct);
}

/// <summary>
/// Files for synced records, local-first. Import encrypts a file into chunks on disk at once (the record that uses
/// it can be saved immediately), and the engine uploads them before the record. Reads use the local chunks and fetch
/// missing ones from the server. Chunks on disk and on the server are the same ciphertext:
/// <c>AES-256-GCM(key, chunk padded, AAD "helm-blob/v1|&lt;id&gt;|&lt;index&gt;")</c> as [nonce 12][tag 16][data]. The key
/// is random per file and lives only in the <see cref="BlobRef"/>, which the caller stores in an encrypted record.
/// </summary>
public sealed class BlobStore : IBlobSync
{
    /// <summary>Plaintext bytes per chunk. Part of the format: never change it for existing blobs.</summary>
    public const int ChunkSize = 4 * 1024 * 1024;
    private const int PadTo = 64 * 1024;
    private const int Overhead = 12 + 16;
    /// <summary>Blobs no record uses are deleted only after this long, so another device's upload in progress is safe.</summary>
    public static readonly TimeSpan GarbageGrace = TimeSpan.FromDays(7);
    public const long DefaultMaxBlobBytes = 256L * 1024 * 1024;

    private readonly SyncDatabase _db;
    private readonly IBlobTransport _transport;
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _upload = new(1, 1);
    private SyncLimits? _limits;

    public BlobStore(SyncDatabase db, IBlobTransport transport, string directory, TimeProvider? time = null, ILogger<BlobStore>? logger = null)
    {
        _db = db;
        _transport = transport;
        _directory = directory;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Directory.CreateDirectory(directory);
    }

    /// <summary>Raised on any thread after a pending blob was committed on the server.</summary>
    public event EventHandler<string>? Uploaded;

    /// <summary>
    /// Encrypts <paramref name="source"/> into a new local blob and returns its reference. The upload happens with the
    /// next sync. The plaintext never touches the disk.
    /// </summary>
    /// <exception cref="BlobTooLargeException">Larger than the server accepts.</exception>
    public async Task<BlobRef> ImportAsync(Stream source, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var length = source.CanSeek ? source.Length - source.Position : -1;
        var max = await MaxBlobBytesAsync(ct).ConfigureAwait(false);
        if (length > max) throw new BlobTooLargeException(length, max);

        var id = SyncIds.NewId(_time);
        var key = RandomNumberGenerator.GetBytes(32);
        var folder = Folder(id);
        Directory.CreateDirectory(folder);
        var buffer = new byte[ChunkSize];
        long total = 0, cipherSize = 0;
        var index = 0;
        try
        {
            while (true)
            {
                var read = await ReadFullAsync(source, buffer, ct).ConfigureAwait(false);
                if (read == 0 && index > 0) break;
                total += read;
                if (total > max) throw new BlobTooLargeException(total, max);
                // The last chunk is padded to a multiple of 64 KiB, so the server learns the size only roughly.
                var padded = read == ChunkSize ? ChunkSize : Math.Max(PadTo, (read + PadTo - 1) / PadTo * PadTo);
                Array.Clear(buffer, read, padded - read);
                var sealedChunk = Seal(key, id, index, buffer.AsSpan(0, padded));
                await WriteAtomicAsync(ChunkPath(id, index), sealedChunk, ct).ConfigureAwait(false);
                cipherSize += sealedChunk.Length;
                index++;
                if (length > 0) progress?.Report(Math.Min(1, (double)total / length));
                if (read < ChunkSize) break;
            }
            CryptographicOperations.ZeroMemory(buffer);
            var now = Now();
            _db.UpsertBlob(new LocalBlob(id, cipherSize, index, LocalBlobState.Pending, now, now));
            _logger.LogInformation("Imported blob {Id} ({Chunks} chunks)", id, index);
            return new BlobRef(id, total, ChunkSize, index, key);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(buffer);
            TryDeleteFolder(folder);
            throw;
        }
    }

    /// <summary>Decrypts the blob into <paramref name="destination"/>, fetching chunks the device does not have.</summary>
    /// <exception cref="BlobUnavailableException">A chunk is on neither side.</exception>
    /// <exception cref="CryptographicException">A chunk was tampered with or belongs to another blob.</exception>
    public async Task ReadAsync(BlobRef blob, Stream destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await DecryptAsync(blob, i => LoadChunkAsync(blob.Id, i, ct), destination, progress, ct).ConfigureAwait(false);
        if (_db.GetBlob(blob.Id) is { } local) _db.UpsertBlob(local with { LastAccessMs = Now() });
    }

    /// <summary>Decrypts a blob whose sealed chunks come from anywhere (a backup repository, the restore tool).</summary>
    /// <exception cref="CryptographicException">A chunk was tampered with, is missing data, or belongs to another blob.</exception>
    public static async Task DecryptAsync(BlobRef blob, Func<int, Task<byte[]>> readChunk, Stream destination,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        long remaining = blob.Size;
        for (var index = 0; index < blob.ChunkCount; index++)
        {
            var sealedChunk = await readChunk(index).ConfigureAwait(false);
            var plain = Open(blob.Key, blob.Id, index, sealedChunk);
            try
            {
                var take = (int)Math.Min(remaining, plain.Length);
                await destination.WriteAsync(plain.AsMemory(0, take), ct).ConfigureAwait(false);
                remaining -= take;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
            progress?.Report((index + 1.0) / blob.ChunkCount);
        }
        if (remaining != 0) throw new CryptographicException("The file is shorter than its reference says.");
    }

    /// <summary>Reads the whole file into memory (small documents and previews).</summary>
    public async Task<byte[]> ReadAllAsync(BlobRef blob, CancellationToken ct = default)
    {
        using var memory = new MemoryStream(checked((int)blob.Size));
        await ReadAsync(blob, memory, null, ct).ConfigureAwait(false);
        return memory.ToArray();
    }

    /// <summary>Whether every chunk is on this device (the file opens offline).</summary>
    public bool IsCached(BlobRef blob) => Enumerable.Range(0, blob.ChunkCount).All(i => File.Exists(ChunkPath(blob.Id, i)));

    /// <summary>Downloads every chunk now, so the file opens offline.</summary>
    public async Task CacheAsync(BlobRef blob, CancellationToken ct = default)
    {
        for (var index = 0; index < blob.ChunkCount; index++) await LoadChunkAsync(blob.Id, index, ct).ConfigureAwait(false);
    }

    /// <summary>One chunk exactly as stored (ciphertext), from the cache or the server: what backups copy.</summary>
    public Task<byte[]> ReadSealedChunkAsync(string id, int index, CancellationToken ct = default) => LoadChunkAsync(id, index, ct);

    /// <summary>
    /// Brings back a blob from a backup: its chunks, as they were sealed, under the same id. It is uploaded with the
    /// next sync unless the server still has it. Chunks are checked against <paramref name="blob"/> before anything is
    /// stored. A blob this device already has is left alone.
    /// </summary>
    public async Task ImportSealedAsync(BlobRef blob, Func<int, Task<byte[]>> readChunk, CancellationToken ct = default)
    {
        if (_db.GetBlob(blob.Id) is not null && IsCached(blob)) return;
        Directory.CreateDirectory(Folder(blob.Id));
        long cipherSize = 0;
        for (var index = 0; index < blob.ChunkCount; index++)
        {
            var sealedChunk = await readChunk(index).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(Open(blob.Key, blob.Id, index, sealedChunk));
            await WriteAtomicAsync(ChunkPath(blob.Id, index), sealedChunk, ct).ConfigureAwait(false);
            cipherSize += sealedChunk.Length;
        }
        var now = Now();
        _db.UpsertBlob(new LocalBlob(blob.Id, cipherSize, blob.ChunkCount, LocalBlobState.Pending, now, now));
    }

    public bool IsPending(string id) => _db.GetBlob(id) is { State: LocalBlobState.Pending };

    public IReadOnlyList<string> PendingIds() => _db.ListBlobs(LocalBlobState.Pending).Select(b => b.Id).ToList();

    public async Task<bool> UploadPendingAsync(CancellationToken ct)
    {
        if (!_transport.IsConfigured) return true;
        await _upload.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var blob in _db.ListBlobs(LocalBlobState.Pending))
            {
                try
                {
                    await UploadAsync(blob, ct).ConfigureAwait(false);
                }
                catch (SyncQuotaException ex)
                {
                    _logger.LogWarning("Blob upload stopped: {Message}", ex.Message);
                    return false;
                }
                catch (SyncRequestException ex)
                {
                    // A refusal specific to this blob (too large for the server, bad request): keep it pending and
                    // move on, so one file never blocks the others.
                    _logger.LogWarning("Blob {Id} was refused by the server: {Code}", blob.Id, ex.Code);
                }
            }
            return true;
        }
        finally
        {
            _upload.Release();
        }
    }

    public async Task CollectGarbageAsync(IReadOnlySet<string> referenced, CancellationToken ct)
    {
        var cutoff = _time.GetUtcNow() - GarbageGrace;
        // Local: chunks of uploaded blobs nothing uses any more (pending ones are kept: they exist nowhere else).
        foreach (var local in _db.ListBlobs(LocalBlobState.Committed))
        {
            if (referenced.Contains(local.Id)) continue;
            TryDeleteFolder(Folder(local.Id));
            _db.DeleteBlob(local.Id);
        }
        if (!_transport.IsConfigured) return;

        string? after = null;
        int deleted = 0, restored = 0;
        do
        {
            var page = await _transport.ListAsync(after, 500, ct).ConfigureAwait(false);
            foreach (var remote in page.Blobs)
            {
                var used = referenced.Contains(remote.Id);
                if (used && remote.DeletedAt is not null)
                {
                    await _transport.RestoreAsync(remote.Id, ct).ConfigureAwait(false);
                    restored++;
                }
                else if (!used && remote.DeletedAt is null && remote.CreatedAt < cutoff && !IsPending(remote.Id))
                {
                    await _transport.DeleteAsync(remote.Id, ct).ConfigureAwait(false);
                    deleted++;
                }
            }
            after = page.HasMore ? page.Next : null;
        } while (after is not null);
        if (deleted + restored > 0) _logger.LogInformation("Blob clean-up: {Deleted} deleted, {Restored} restored", deleted, restored);
    }

    /// <summary>Drops the local chunks of an uploaded blob (they are fetched again when needed).</summary>
    public void Evict(string id)
    {
        if (_db.GetBlob(id) is not { State: LocalBlobState.Committed }) return;
        TryDeleteFolder(Folder(id));
    }

    private async Task UploadAsync(LocalBlob blob, CancellationToken ct)
    {
        var sizes = Enumerable.Range(0, blob.ChunkCount).Select(i => new FileInfo(ChunkPath(blob.Id, i))).ToList();
        if (sizes.Any(f => !f.Exists))
        {
            _logger.LogError("Blob {Id} lost local chunks before it was uploaded; it cannot be uploaded", blob.Id);
            return;
        }
        var remote = await _transport.ReserveAsync(blob.Id, blob.CipherSize, blob.ChunkCount, ct).ConfigureAwait(false);
        if (remote.State != RemoteBlobState.Committed)
        {
            var have = remote.Chunks.ToHashSet();
            for (var index = 0; index < blob.ChunkCount; index++)
            {
                if (have.Contains(index)) continue;
                var data = await File.ReadAllBytesAsync(ChunkPath(blob.Id, index), ct).ConfigureAwait(false);
                await _transport.PutChunkAsync(blob.Id, index, data, ct).ConfigureAwait(false);
            }
            await _transport.CommitAsync(blob.Id, ct).ConfigureAwait(false);
        }
        _db.UpsertBlob(blob with { State = LocalBlobState.Committed });
        _logger.LogInformation("Uploaded blob {Id}", blob.Id);
        try { Uploaded?.Invoke(this, blob.Id); }
        catch (Exception ex) { _logger.LogError(ex, "A blob upload handler failed"); }
    }

    private async Task<byte[]> LoadChunkAsync(string id, int index, CancellationToken ct)
    {
        var path = ChunkPath(id, index);
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (!_transport.IsConfigured) throw new BlobUnavailableException("This file is not on this device and sync is off.");
        var data = await _transport.GetChunkAsync(id, index, ct).ConfigureAwait(false)
            ?? throw new BlobUnavailableException("This file is not on the server yet. Open it on the device it was added on, and let it sync.");
        Directory.CreateDirectory(Folder(id));
        await WriteAtomicAsync(path, data, ct).ConfigureAwait(false);
        if (_db.GetBlob(id) is null)
        {
            var now = Now();
            _db.UpsertBlob(new LocalBlob(id, 0, 0, LocalBlobState.Committed, now, now));
        }
        return data;
    }

    private async Task<long> MaxBlobBytesAsync(CancellationToken ct)
    {
        if (_limits is not null) return Math.Min(_limits.MaxBlobBytes, (long)_limits.MaxChunksPerBlob * ChunkSize);
        if (!_transport.IsConfigured) return DefaultMaxBlobBytes;
        try
        {
            _limits = await _transport.GetLimitsAsync(ct).ConfigureAwait(false);
            // The server counts encrypted chunks; in plaintext that is its chunk count times this store's chunk size.
            return Math.Min(_limits.MaxBlobBytes, (long)_limits.MaxChunksPerBlob * ChunkSize);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return DefaultMaxBlobBytes;
        }
    }

    internal static byte[] Seal(byte[] key, string id, int index, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[Overhead + plaintext.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(output.AsSpan(0, 12), plaintext, output.AsSpan(Overhead), output.AsSpan(12, 16), Aad(id, index));
        return output;
    }

    internal static byte[] Open(byte[] key, string id, int index, byte[] sealedChunk)
    {
        if (sealedChunk.Length < Overhead) throw new CryptographicException("A chunk of this file is damaged.");
        var plain = new byte[sealedChunk.Length - Overhead];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(sealedChunk.AsSpan(0, 12), sealedChunk.AsSpan(Overhead), sealedChunk.AsSpan(12, 16), plain, Aad(id, index));
        return plain;
    }

    private static byte[] Aad(string id, int index) => Encoding.UTF8.GetBytes($"helm-blob/v1|{id}|{index}");

    private static async Task<int> ReadFullAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static async Task WriteAtomicAsync(string path, byte[] data, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private string Folder(string id) => Path.Combine(_directory, id);

    private string ChunkPath(string id, int index) => Path.Combine(Folder(id), $"{index}.chunk");

    private void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Folder}", folder);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Folder}", folder);
        }
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();
}
