using System.Text.Json.Serialization;

namespace Helm.Modules.WatchLater;

public static class WatchLaterIds
{
    /// <summary>Module id on both apps: the settings file (watch-later.json) and the enabled-state key.</summary>
    public const string ModuleId = "watch-later";

    public const string DisplayName = "Watch Later";

    public const string Description = "Save YouTube and Facebook videos, Shorts and Reels to watch later, synced across your devices. Download the ones you want to keep.";
}

public enum WatchSource
{
    YouTube,
    Facebook,
    /// <summary>Any other link: saved as it is, without looking up its title.</summary>
    Other,
}

public enum WatchKind
{
    Video,
    /// <summary>A YouTube Short or a Facebook Reel.</summary>
    Short,
}

/// <summary>One saved video (synced record in <c>watch.items</c>).</summary>
public sealed record WatchItem
{
    public const int MaxNoteLength = 2_000;

    /// <summary>
    /// What makes two links the same video, e.g. "youtube:dQw4w9WgXcQ": a Short and the same video as a normal link
    /// share it. Saving a link that is already in the list moves that video to the top instead of adding it again.
    /// </summary>
    public string Key { get; init; } = "";

    /// <summary>The cleaned-up link (tracking parameters removed) that Open uses.</summary>
    public string Url { get; init; } = "";

    /// <summary>The link as it was shared.</summary>
    public string OriginalUrl { get; init; } = "";

    public WatchSource Source { get; init; }

    public WatchKind Kind { get; init; }

    /// <summary>The video id on its site, when the link has one.</summary>
    public string? ExternalId { get; init; }

    public string? Title { get; init; }

    /// <summary>The channel or page that posted it.</summary>
    public string? Channel { get; init; }

    public string? ThumbnailUrl { get; init; }

    public int? DurationSeconds { get; init; }

    /// <summary>Why it was saved; optional.</summary>
    public string Note { get; init; } = "";

    public bool Watched { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    public DateTimeOffset? WatchedAt { get; init; }

    /// <summary>True once the title and the rest were looked up (or there is nothing to look up).</summary>
    public bool MetadataDone { get; init; }

    /// <summary>
    /// A phone asked a PC to download the video, in this quality (a <see cref="VideoQuality"/> id); cleared when a PC has
    /// downloaded it or the request is cancelled.
    /// </summary>
    public string? DownloadRequest { get; init; }

    /// <summary>The device that downloaded it for a request, e.g. "HUNG-PC".</summary>
    public string? DownloadedOn { get; init; }

    public DateTimeOffset? DownloadedAt { get; init; }

    /// <summary>Where watching stopped, in seconds (synced, so another device carries on there); null from the start.</summary>
    public int? ResumeSeconds { get; init; }

    /// <summary>The title, or else the note, or else the link.</summary>
    [JsonIgnore]
    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(Title) ? Title.Trim()
        : Note.Trim().Length > 0 ? WatchLaterFormat.Shorten(Note.Trim().Split('\n')[0], 100)
        : Url;
}

/// <summary>Which videos the list shows.</summary>
public enum WatchFilter
{
    /// <summary>Everything not watched yet.</summary>
    ToWatch,
    Videos,
    Shorts,
    Watched,
}
