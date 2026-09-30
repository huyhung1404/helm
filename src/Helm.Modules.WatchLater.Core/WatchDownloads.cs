namespace Helm.Modules.WatchLater;

public enum DownloadState
{
    None,
    /// <summary>Waiting for another download to finish.</summary>
    Queued,
    Downloading,
    Downloaded,
    Failed,
}

/// <summary>Where a download on this device stands.</summary>
/// <param name="Progress">0 to 1 while downloading, when known.</param>
/// <param name="Text">"Downloading 45 %", "Could not download: …".</param>
public sealed record DownloadStatus(DownloadState State, double? Progress = null, string Text = "")
{
    public static DownloadStatus None { get; } = new(DownloadState.None);
}

/// <summary>
/// Downloading saved videos. Windows downloads on the PC itself (yt-dlp); Android cannot, so a download there is a
/// request that a PC picks up (<see cref="WatchItem.DownloadRequest"/>, synced).
/// </summary>
public interface IWatchDownloads
{
    /// <summary>True when videos are downloaded on this device; false when Download asks a PC to do it.</summary>
    bool DownloadsHere { get; }

    /// <summary>The qualities to choose from before a download (may look the video up, a few seconds).</summary>
    Task<IReadOnlyList<QualityOption>> QualitiesAsync(WatchItem item, CancellationToken ct);

    /// <summary>Downloads the video (or asks a PC to) in a <see cref="VideoQuality"/>.</summary>
    void Start(string id, string quality);

    /// <summary>Stops a download (or withdraws the request).</summary>
    void Cancel(string id);

    /// <summary>The download on this device.</summary>
    DownloadStatus Status(string id);

    /// <summary>The downloaded file on this device, if it is still there.</summary>
    string? FileFor(string id);

    /// <summary>Forgets a video (it was deleted): stops its download; the file stays.</summary>
    void Forget(string id);

    /// <summary>True when ffmpeg is missing here, so most YouTube qualities cannot be downloaded.</summary>
    bool NeedsFfmpeg { get; }

    /// <summary>Installs ffmpeg (Windows only).</summary>
    Task InstallFfmpegAsync(IProgress<double> progress, CancellationToken ct);

    /// <summary>Raised when a download's status changes (any thread), with the video's id.</summary>
    event EventHandler<string>? StatusChanged;
}

/// <summary>
/// Android: videos are not downloaded on the phone. Download marks the video for a PC, which downloads it in the chosen
/// quality the next time it syncs and shows "Downloaded on …" here.
/// </summary>
public sealed class RemoteDownloads(WatchLaterStore store) : IWatchDownloads
{
    public bool DownloadsHere => false;

    public Task<IReadOnlyList<QualityOption>> QualitiesAsync(WatchItem item, CancellationToken ct) => Task.FromResult(VideoQuality.Presets);

    public void Start(string id, string quality)
    {
        store.RequestDownload(id, quality);
        StatusChanged?.Invoke(this, id);
    }

    public void Cancel(string id)
    {
        store.RequestDownload(id, null);
        StatusChanged?.Invoke(this, id);
    }

    public DownloadStatus Status(string id) => DownloadStatus.None;

    public string? FileFor(string id) => null;

    public void Forget(string id) { }

    public bool NeedsFfmpeg => false;

    public Task InstallFfmpegAsync(IProgress<double> progress, CancellationToken ct) => throw new NotSupportedException("Videos are downloaded on a PC.");

    public event EventHandler<string>? StatusChanged;
}
