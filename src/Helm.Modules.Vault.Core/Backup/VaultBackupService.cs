using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.Vault.Backup;

/// <summary>Where this device writes its backup repository (from <see cref="VaultSettings.BackupLocation"/>).</summary>
public interface IVaultBackupLocation
{
    /// <returns>Null when no backup location is set, or it is not reachable right now (unplugged, permission gone).</returns>
    IBackupTarget? Open(string? location);
}

/// <summary>PC: the location is a folder path.</summary>
public sealed class FolderBackupLocation : IVaultBackupLocation
{
    public IBackupTarget? Open(string? location) =>
        !string.IsNullOrWhiteSpace(location) && Directory.Exists(location) ? new FolderBackupTarget(location) : null;
}

/// <param name="ChunksRepaired">Chunks the backup held damaged (bit rot, a sync client's mistake) and that were written again.</param>
public sealed record VaultBackupResult(string Snapshot, bool Written, int Records, int Blobs, int ChunksCopied, int SnapshotsPruned, int ChunksRepaired = 0);

/// <summary>
/// Backs the vault up into an append-only repository in a folder the user picked, verifies what it wrote, and
/// prunes old snapshots (docs/vault-design.md, I7). Needs the vault unlocked: snapshots are sealed with a key derived
/// from the vault key. Restores a whole vault onto a new device, or the items a vault is missing.
/// </summary>
/// <summary>A device's last good backup of the vault (synced, so one device backing up is enough for all of them).</summary>
public sealed record VaultBackupMark(string Device, long LastGoodBackupMs);

public sealed class VaultBackupService
{
    /// <summary>One <see cref="VaultBackupMark"/> per device.</summary>
    public const string MarksCollection = "vault.backups";

    private readonly VaultSession _session;
    private readonly VaultStore _store;
    private readonly BlobStore _blobs;
    private readonly IVaultBackupLocation _location;
    private readonly ISettingsStore<VaultSettings> _settings;
    private readonly ISettingsStore<VaultDeviceState> _device;
    private readonly IDeviceName _deviceName;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISyncedCollection<VaultBackupMark>? _marks;

    public VaultBackupService(VaultSession session, VaultStore store, BlobStore blobs, IVaultBackupLocation location,
        ISettingsStoreFactory settings, IDeviceName? deviceName = null, TimeProvider? time = null, ILogger<VaultBackupService>? logger = null,
        ISyncedCollection<VaultBackupMark>? marks = null)
    {
        _marks = marks;
        _session = session;
        _store = store;
        _blobs = blobs;
        _location = location;
        _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
        _device = settings.Get<VaultDeviceState>(VaultDeviceState.StoreId);
        _deviceName = deviceName ?? new FixedDeviceName(Environment.MachineName);
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Raised on any thread after a backup succeeded or failed.</summary>
    public event EventHandler? Completed;

    public DateTimeOffset? LastGoodBackup =>
        _device.Current.LastGoodBackupMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(_device.Current.LastGoodBackupMs) : null;

    public string? LastError => _device.Current.LastBackupError;

    /// <summary>A day passed since the last good backup (or there never was one) and a location is set.</summary>
    public bool IsDue => _settings.Current.BackupLocation is not null
        && (LastGoodBackup is not { } last || _time.GetUtcNow() - last > TimeSpan.FromHours(20));

    /// <summary>The latest good backup made by any device of the account (this one included).</summary>
    public DateTimeOffset? LastGoodBackupAnywhere
    {
        get
        {
            var latest = _device.Current.LastGoodBackupMs;
            if (_marks is not null)
            {
                try
                {
                    foreach (var mark in _marks.All()) latest = Math.Max(latest, mark.Value.LastGoodBackupMs);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not read the other devices' backups"); }
            }
            return latest > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(latest) : null;
        }
    }

    /// <summary>
    /// No device made a good backup for a week: the UI warns. One device with a backup folder is enough; the others
    /// do not need one.
    /// </summary>
    public bool IsOverdue => LastGoodBackupAnywhere is not { } last || _time.GetUtcNow() - last > TimeSpan.FromDays(7);

    /// <summary>Backs up now. A snapshot is written only when the vault changed since the latest one, which is re-verified either way.</summary>
    /// <exception cref="InvalidOperationException">No location set, or it is not reachable.</exception>
    /// <exception cref="VaultLockedException">The vault is locked.</exception>
    public async Task<VaultBackupResult> BackUpAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await BackUpCoreAsync(ct).ConfigureAwait(false);
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            _device.Update(d =>
            {
                d.LastGoodBackupMs = now;
                d.LastBackupError = null;
            });
            ShareMark(now);
            _logger.LogInformation("Vault backup {Snapshot}: written {Written}, {Records} records, {Chunks} chunks copied, {Pruned} pruned",
                result.Snapshot, result.Written, result.Records, result.ChunksCopied, result.SnapshotsPruned);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not VaultLockedException)
        {
            _device.Update(d => d.LastBackupError = ex.Message);
            _logger.LogError(ex, "Vault backup failed");
            throw;
        }
        finally
        {
            _gate.Release();
            try { Completed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.LogError(ex, "A backup handler failed"); }
        }
    }

    /// <summary>Tells the account's other devices that this one has a good backup (they stop warning).</summary>
    private void ShareMark(long at)
    {
        if (_marks is null) return;
        try
        {
            if (_device.Current.BackupDeviceId is not { Length: > 0 } id)
            {
                id = Guid.NewGuid().ToString("N");
                _device.Update(d => d.BackupDeviceId = id);
            }
            _marks.Upsert(id, new VaultBackupMark(_deviceName.DeviceName, at));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not share the backup time with the other devices"); }
    }

    /// <summary>A backup location picked by the user (e.g. to restore from), through the platform's location type.</summary>
    public IBackupTarget? OpenLocation(string location) => _location.Open(location);

    /// <summary>For timers and unlock hooks: backs up if due, never throws.</summary>
    public async Task BackUpIfDueAsync(CancellationToken ct = default)
    {
        if (!IsDue || _session.State != VaultState.Unlocked) return;
        try
        {
            await BackUpAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled vault backup did not run");
        }
    }

    /// <summary>
    /// Restores a whole vault from a backup on a device (and account) that has none: the same keyring, key, items and
    /// documents as at that snapshot. Afterwards the vault is unlocked and syncs up as usual.
    /// </summary>
    /// <param name="mustChangePassword">The backup was opened with the recovery key: ask for a new password next.</param>
    public async Task<int> RestoreVaultAsync(VaultBackupRepository repository, string snapshotName, VaultKey key, CancellationToken ct = default,
        bool mustChangePassword = false)
    {
        if (!repository.Keyring.Matches(key)) throw new VaultKeyException("This key does not open this backup.");
        await _session.SyncBeforeCreatingAsync(ct).ConfigureAwait(true);
        if (_session.Keyring is not null) throw new VaultKeyException("This account already has a vault. Restore the missing items into it instead.");
        var snapshot = await repository.ReadSnapshotAsync(snapshotName, key, ct).ConfigureAwait(true);
        // Files first, so every item that comes back has its documents (checked chunk by chunk against the item).
        foreach (var blob in RefsIn(snapshot, key)) await _blobs.ImportSealedAsync(blob, i => repository.ReadChunkAsync(blob.Id, i, ct), ct).ConfigureAwait(true);
        _session.AdoptRestoredVault(repository.Keyring, new VaultKey(key.Bytes), mustChangePassword);
        foreach (var record in snapshot.Records) _store.RestoreSealed(record.Id, record.Record);
        _logger.LogWarning("Restored {Count} vault records from backup {Snapshot}", snapshot.Records.Count, snapshotName);
        return snapshot.Records.Count;
    }

    /// <summary>
    /// Brings back the items of a snapshot that the vault no longer has (deleted for good, or lost), with their
    /// documents. Items the vault still has are left alone. The backup may be of another vault: its items are opened
    /// with <paramref name="backupKey"/> and saved into this one.
    /// </summary>
    /// <returns>How many items came back.</returns>
    public async Task<int> RestoreMissingAsync(VaultBackupRepository repository, string snapshotName, VaultKey backupKey, CancellationToken ct = default)
    {
        _ = _session.Key;
        var snapshot = await repository.ReadSnapshotAsync(snapshotName, backupKey, ct).ConfigureAwait(true);
        var present = _store.SealedRecords().Select(r => r.Value.Uid).ToHashSet(StringComparer.Ordinal);
        var sameVault = repository.VaultId == _session.VaultId;
        var restored = 0;
        foreach (var group in snapshot.Records.GroupBy(r => r.Record.Uid))
        {
            if (present.Contains(group.Key)) continue;
            // Same vault: every record comes back as it was (conflict copies included). Another vault: its main
            // version, sealed again with this vault's key.
            var records = sameVault ? group.ToList() : group.OrderBy(r => r.Id == group.Key ? 0 : 1).Take(1).ToList();
            var any = false;
            foreach (var record in records)
            {
                var item = VaultStore.TryOpenWith(backupKey, record.Record);
                if (item is null) continue;
                foreach (var blob in item.AllBlobs())
                    await _blobs.ImportSealedAsync(blob, i => repository.ReadChunkAsync(blob.Id, i, ct), ct).ConfigureAwait(true);
                if (sameVault) _store.RestoreSealed(record.Id, record.Record);
                else _store.AddRestored(group.Key, item, record.Record.Trashed, record.Record.TrashedAtMs);
                any = true;
            }
            if (any) restored++;
        }
        _logger.LogWarning("Restored {Count} missing vault items from backup {Snapshot}", restored, snapshotName);
        return restored;
    }

    private async Task<VaultBackupResult> BackUpCoreAsync(CancellationToken ct)
    {
        var key = _session.Key;
        var keyring = _session.Keyring ?? throw new InvalidOperationException("Create the vault first.");
        var target = _location.Open(_settings.Current.BackupLocation)
            ?? throw new InvalidOperationException("The backup folder is not set or not reachable. Check the Backup settings.");
        var directory = VaultBackupRepository.DirectoryFor(keyring.VaultId);

        // 1. What the vault is now.
        var records = _store.SealedRecords().Select(r => new SnapshotRecord(r.Id, r.Value)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var blobs = _store.ReferencedBlobs();
        var digest = VaultBackupRepository.DigestOf(records);

        // 2. Keyring and readme (small; rewritten whenever they differ).
        var keyringJson = VaultBackupRepository.KeyringJson(keyring);
        if (await target.ReadAsync($"{directory}/keyring.json", ct).ConfigureAwait(false) is not { } stored || !stored.AsSpan().SequenceEqual(keyringJson))
            await target.WriteAsync($"{directory}/keyring.json", keyringJson, ct).ConfigureAwait(false);
        if (!await target.ExistsAsync($"{directory}/README.txt", ct).ConfigureAwait(false))
            await target.WriteAsync($"{directory}/README.txt", System.Text.Encoding.UTF8.GetBytes(VaultBackupRepository.Readme), ct).ConfigureAwait(false);

        // 3. Chunks the repository does not have yet: copied, then read back and compared.
        var copied = 0;
        foreach (var blob in blobs)
        {
            var present = (await target.ListFilesAsync($"{directory}/blobs/{blob.Id}", ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < blob.ChunkCount; index++)
            {
                if (present.Contains($"{index}.chunk")) continue;
                var chunk = await _blobs.ReadSealedChunkAsync(blob.Id, index, ct).ConfigureAwait(false);
                var path = VaultBackupRepository.ChunkPath(directory, blob.Id, index);
                await target.WriteAsync(path, chunk, ct).ConfigureAwait(false);
                var back = await target.ReadAsync(path, ct).ConfigureAwait(false);
                if (back is null || !back.AsSpan().SequenceEqual(chunk)) throw new IOException($"The backup folder did not keep a chunk of a document ({path}).");
                copied++;
            }
        }

        // 3b. Chunks copied on earlier days are decrypted again, a random share per run (64 MiB): a chunk that rotted in
        // the backup is found long before a restore would need it, and rewritten from this device or the server.
        var repaired = await DeepVerifyAsync(target, directory, blobs, copiedThisRun: copied, ct).ConfigureAwait(false);

        // 4. The snapshot, unless the latest one already holds exactly this.
        var repository = await VaultBackupRepository.OpenAsync(target, directory, ct).ConfigureAwait(false)
            ?? throw new IOException("The backup folder did not keep the keyring.");
        string name;
        var written = false;
        VaultSnapshot? latest = null;
        if (repository.Snapshots.Count > 0)
        {
            try
            {
                latest = await repository.ReadSnapshotAsync(repository.Snapshots[0], key, ct).ConfigureAwait(false);
            }
            catch (VaultKeyException ex)
            {
                _logger.LogWarning("The latest vault snapshot {Name} is unreadable ({Reason}); writing a new one", repository.Snapshots[0], ex.Message);
            }
        }
        if (latest?.Digest == digest)
        {
            name = repository.Snapshots[0];
        }
        else
        {
            name = VaultBackupRepository.SnapshotName(_time.GetUtcNow());
            var snapshot = new VaultSnapshot(VaultSnapshot.CurrentFormat, keyring.VaultId, _time.GetUtcNow().ToUnixTimeMilliseconds(),
                _deviceName.DeviceName, records, blobs.Select(b => new SnapshotBlob(b.Id, b.ChunkCount)).ToList(), digest);
            await target.WriteAsync($"{directory}/snapshots/{name}", VaultBackupRepository.Seal(snapshot, name, key), ct).ConfigureAwait(false);
            written = true;
        }

        // 5. Verify (I7): the snapshot reads back, decrypts, matches, and every chunk it needs is there.
        var verified = await repository.ReadSnapshotAsync(name, key, ct).ConfigureAwait(false);
        if (verified.Digest != digest || verified.Records.Count != records.Count) throw new IOException("The backup did not verify: the snapshot read back differs.");
        await VerifyChunksAsync(target, directory, verified.Blobs, ct).ConfigureAwait(false);

        // 6. Only now: prune old snapshots, then the chunks no kept snapshot needs.
        var pruned = await PruneAsync(target, directory, key, ct).ConfigureAwait(false);
        await target.DeleteStaleTempFilesAsync(directory, TimeSpan.FromDays(1), ct).ConfigureAwait(false);
        return new VaultBackupResult(name, written, records.Count, blobs.Count, copied, pruned, repaired);
    }

    private const long DeepVerifyBudget = 64L * 1024 * 1024;

    private async Task<int> DeepVerifyAsync(IBackupTarget target, string directory, IReadOnlyList<BlobRef> blobs, int copiedThisRun, CancellationToken ct)
    {
        var chunks = blobs.SelectMany(b => Enumerable.Range(0, b.ChunkCount).Select(i => (Blob: b, Index: i))).OrderBy(_ => Random.Shared.Next()).ToList();
        long read = 0;
        var repaired = 0;
        foreach (var (blob, index) in chunks)
        {
            if (read >= DeepVerifyBudget) break;
            var path = VaultBackupRepository.ChunkPath(directory, blob.Id, index);
            var stored = await target.ReadAsync(path, ct).ConfigureAwait(false);
            read += stored?.Length ?? 0;
            if (stored is not null && BlobStore.IsIntact(blob, index, stored)) continue;
            var good = await _blobs.ReadSealedChunkAsync(blob.Id, index, ct).ConfigureAwait(false);
            if (!BlobStore.IsIntact(blob, index, good))
                throw new IOException($"A chunk of a document is damaged in the backup and on this device ({blob.Id}/{index}).");
            await target.WriteAsync(path, good, ct).ConfigureAwait(false);
            if (await target.ReadAsync(path, ct).ConfigureAwait(false) is not { } back || !back.AsSpan().SequenceEqual(good))
                throw new IOException($"The backup folder did not keep a repaired chunk ({path}).");
            _logger.LogWarning("Backup chunk {Blob}/{Index} was damaged; rewritten", blob.Id, index);
            repaired++;
        }
        return repaired;
    }

    private static async Task VerifyChunksAsync(IBackupTarget target, string directory, IReadOnlyList<SnapshotBlob> blobs, CancellationToken ct)
    {
        foreach (var blob in blobs)
        {
            var present = (await target.ListFilesAsync($"{directory}/blobs/{blob.Id}", ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < blob.ChunkCount; index++)
            {
                if (!present.Contains($"{index}.chunk")) throw new IOException($"The backup did not verify: a chunk of a document is missing ({blob.Id}/{index}).");
            }
        }
    }

    /// <summary>Keeps the newest snapshot of each of the last N days and of each of the last M months, and the newest overall.</summary>
    internal static IReadOnlySet<string> SnapshotsToKeep(IReadOnlyList<string> names, int keepDaily, int keepMonthly)
    {
        var dated = names.Select(n => (Name: n, At: VaultBackupRepository.SnapshotTime(n)!.Value)).OrderByDescending(s => s.At).ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (dated.Count > 0) keep.Add(dated[0].Name);
        foreach (var day in dated.GroupBy(s => s.At.UtcDateTime.Date).Take(Math.Max(1, keepDaily))) keep.Add(day.First().Name);
        foreach (var month in dated.GroupBy(s => (s.At.Year, s.At.Month)).Take(Math.Max(0, keepMonthly))) keep.Add(month.First().Name);
        return keep;
    }

    private async Task<int> PruneAsync(IBackupTarget target, string directory, VaultKey key, CancellationToken ct)
    {
        var repository = await VaultBackupRepository.OpenAsync(target, directory, ct).ConfigureAwait(false);
        if (repository is null) return 0;
        var keep = SnapshotsToKeep(repository.Snapshots, _settings.Current.BackupKeepDaily, _settings.Current.BackupKeepMonthly);

        // The blobs every kept snapshot needs. If any kept snapshot cannot be read, nothing is deleted at all.
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in keep)
        {
            try
            {
                foreach (var blob in (await repository.ReadSnapshotAsync(name, key, ct).ConfigureAwait(false)).Blobs) needed.Add(blob.Id);
            }
            catch (VaultKeyException ex)
            {
                _logger.LogWarning("Not pruning the vault backup: snapshot {Name} is unreadable ({Reason})", name, ex.Message);
                return 0;
            }
        }

        var pruned = 0;
        foreach (var name in repository.Snapshots.Where(n => !keep.Contains(n)))
        {
            await target.DeleteAsync($"{directory}/snapshots/{name}", ct).ConfigureAwait(false);
            pruned++;
        }
        foreach (var blobId in await target.ListDirectoriesAsync($"{directory}/blobs", ct).ConfigureAwait(false))
        {
            if (!needed.Contains(blobId)) await target.DeleteDirectoryAsync($"{directory}/blobs/{blobId}", ct).ConfigureAwait(false);
        }
        return pruned;
    }

    /// <summary>The files a snapshot's items use, with their keys (the snapshot itself lists only ids).</summary>
    private static IReadOnlyList<BlobRef> RefsIn(VaultSnapshot snapshot, VaultKey key) =>
        snapshot.Records.Select(r => VaultStore.TryOpenWith(key, r.Record)).Where(i => i is not null)
            .SelectMany(i => i!.AllBlobs()).DistinctBy(b => b.Id).ToList();

    private sealed class FixedDeviceName(string name) : IDeviceName
    {
        public string DeviceName => name;
    }
}

/// <summary>The name stamped on snapshots, e.g. the PC or phone name.</summary>
public interface IDeviceName
{
    string DeviceName { get; }
}
