using System.Globalization;

namespace Helm.Modules.WatchLater;

/// <summary>Display text for Watch Later, in the current culture.</summary>
public static class WatchLaterFormat
{
    /// <summary>"0:42", "12:05", "1:02:03".</summary>
    public static string Duration(int seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>"just now", "5 min ago", "3 h ago", "Yesterday", "28 Sep" (this year) or "28 Sep 2025".</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (local.Date == today) return $"{(int)ago.TotalHours} h ago";
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        return local.Year == today.Year
            ? local.ToString("d MMM", CultureInfo.CurrentCulture)
            : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>"12.3 MB", "1.4 GB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    public static string SourceName(WatchSource source) => source switch
    {
        WatchSource.YouTube => "YouTube",
        WatchSource.Facebook => "Facebook",
        _ => "Link",
    };

    /// <summary>"YouTube Short", "Facebook Reel", "YouTube video", "Link".</summary>
    public static string KindName(WatchSource source, WatchKind kind) => (source, kind) switch
    {
        (WatchSource.YouTube, WatchKind.Short) => "YouTube Short",
        (WatchSource.Facebook, WatchKind.Short) => "Facebook Reel",
        (WatchSource.YouTube, _) => "YouTube video",
        (WatchSource.Facebook, _) => "Facebook video",
        _ => "Link",
    };

    /// <summary>
    /// The link to open outside Helm, carrying on where watching stopped (YouTube videos only: Shorts and Facebook
    /// links have no start time).
    /// </summary>
    public static string ResumeUrl(WatchItem item) =>
        item is { Source: WatchSource.YouTube, Kind: WatchKind.Video, ResumeSeconds: int s and > 0 }
            ? item.Url + (item.Url.Contains('?') ? "&" : "?") + "t=" + s.ToString(CultureInfo.InvariantCulture) + "s"
            : item.Url;

    public static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>A file name without characters Windows forbids.</summary>
    public static string FileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var name = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) name = "video";
        return Shorten(name, 120).Replace("…", "");
    }
}
