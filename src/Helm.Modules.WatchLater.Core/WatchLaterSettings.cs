using Helm.Core.Settings;

namespace Helm.Modules.WatchLater;

/// <summary>
/// Device-local preferences (the saved videos themselves are synced through <see cref="WatchLaterStore"/>). Stored in
/// settings/watch-later.json on both apps.
/// </summary>
public sealed class WatchLaterSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    public WatchFilter Filter { get; set; } = WatchFilter.ToWatch;

    /// <summary>Null shows every site.</summary>
    public WatchSource? SourceFilter { get; set; }

    /// <summary>The quality picked first when downloading (a <see cref="VideoQuality"/> id).</summary>
    public string DefaultQuality { get; set; } = VideoQuality.P1080;

    // ---- Windows only ---------------------------------------------------------------------------------------------

    /// <summary>Where downloads go; null is Videos\Watch Later.</summary>
    public string? DownloadFolder { get; set; }

    /// <summary>Download the videos a phone asks this PC to download.</summary>
    public bool DownloadRequests { get; set; } = true;

    /// <summary>
    /// Let yt-dlp read the sign-in cookies of this browser ("firefox", "chrome", "edge", "brave"), for private and group
    /// videos on Facebook; empty reads none.
    /// </summary>
    public string CookiesBrowser { get; set; } = "";

    /// <summary>Videos downloaded on this PC: item id → file.</summary>
    public Dictionary<string, string> Downloads { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The player window: left, top, width, height (device-independent pixels); null centres it.</summary>
    public double[]? PlayerBounds { get; set; }

    /// <summary>The mini player (always on top): left, top, width, height; null puts it in the bottom-right corner.</summary>
    public double[]? MiniBounds { get; set; }

    /// <summary>The player opens as the mini player.</summary>
    public bool PlayerMini { get; set; }

    /// <summary>When yt-dlp last checked for an update (it breaks when YouTube changes, so it updates itself often).</summary>
    public DateTimeOffset? YtDlpCheckedAt { get; set; }
}
