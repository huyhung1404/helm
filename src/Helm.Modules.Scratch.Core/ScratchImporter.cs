using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.Scratch;

/// <summary>Where an import is: the file (1-based) and how much of it is encrypted (0-1, or null when unknown).</summary>
public sealed record ScratchImportProgress(int Index, int Count, string Name, double? Fraction);

/// <summary>What an import did: the names added, and a sentence per file that could not be added.</summary>
public sealed record ScratchImportResult(IReadOnlyList<string> Added, IReadOnlyList<string> Failed)
{
    /// <summary>"Added “a.jpg”.", "Added 3 files. Could not add “b”: …"</summary>
    public string Summary
    {
        get
        {
            var done = Added.Count switch
            {
                0 => "",
                1 => $"Added “{Added[0]}”.",
                _ => $"Added {Added.Count} files.",
            };
            return Failed.Count == 0 ? done : $"{done} Could not add {string.Join("; ", Failed)}".Trim();
        }
    }
}

/// <summary>
/// Puts files and text into Scratch with their thumbnails and the device's name, then asks for a sync so they reach
/// the other devices. Used by the page and by Android's share target, which runs without the page.
/// </summary>
public sealed class ScratchImporter(ScratchStore store, IScratchPlatform platform, IDeviceInfo device, ISyncService? sync = null,
    ILogger<ScratchImporter>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>One file after the other. A file that fails is reported and the others still go in.</summary>
    public async Task<ScratchImportResult> ImportAsync(IReadOnlyList<ScratchSource> sources, IProgress<ScratchImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var added = new List<string>();
        var failed = new List<string>();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var name = ScratchFormat.SafeFileName(source.Name, ScratchFormat.NameFor(source.MediaType ?? "", store.Now));
            progress?.Report(new ScratchImportProgress(i + 1, sources.Count, name, source.Size is > 0 ? 0 : null));
            try
            {
                var mediaType = ScratchFormat.PickMediaType(source.MediaType, name);
                byte[]? thumbnail = null;
                if (mediaType.StartsWith("image/", StringComparison.Ordinal) || mediaType.StartsWith("video/", StringComparison.Ordinal))
                    thumbnail = await ThumbnailAsync(source, ct).ConfigureAwait(true);
                var index = i;
                await using var opened = source.OpenRead();
                // Counted as it is read: Android's content streams cannot seek, so the store cannot tell the progress.
                await using var content = progress is null || source.Size is not > 0 ? opened
                    : new ProgressStream(opened, source.Size.Value, p => progress.Report(new ScratchImportProgress(index + 1, sources.Count, name, p)));
                await store.AddFileAsync(name, mediaType, content, thumbnail, device.DeviceName, null, ct).ConfigureAwait(true);
                added.Add(name);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Unreadable, too large, revoked permission: say which file and why, and carry on with the next one.
                _logger.LogWarning(ex, "Could not add a file to Scratch");
                failed.Add($"“{name}”: {ex.Message}");
            }
        }
        if (added.Count > 0) sync?.RequestSync();
        return new ScratchImportResult(added, failed);
    }

    /// <exception cref="ArgumentException">The text is empty.</exception>
    public async Task<string> AddTextAsync(string text, CancellationToken ct = default)
    {
        var id = await store.AddTextAsync(text, device.DeviceName, ct).ConfigureAwait(true);
        sync?.RequestSync();
        return id;
    }

    /// <summary>A read-only stream that reports how much of <paramref name="size"/> bytes was read (at most every 1 %).</summary>
    private sealed class ProgressStream(Stream inner, long size, Action<double> report) : Stream
    {
        private long _read;
        private int _reported = -1;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            var percent = (int)Math.Min(100, _read * 100 / Math.Max(1, size));
            if (percent != _reported)
            {
                _reported = percent;
                report(percent / 100.0);
            }
            return read;
        }
    }

    private async Task<byte[]?> ThumbnailAsync(ScratchSource source, CancellationToken ct)
    {
        try
        {
            return await platform.ThumbnailAsync(source, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A picture the platform cannot decode still goes in, without a thumbnail.
            _logger.LogInformation(ex, "No thumbnail for a file in Scratch");
            return null;
        }
    }
}
