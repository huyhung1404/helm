using System.Text.RegularExpressions;

namespace Helm.Modules.WatchLater;

/// <summary>
/// A video link found in shared text, cleaned up: YouTube (videos, Shorts, live, youtu.be), Facebook (videos, watch,
/// Reels, fb.watch and share links) and any other http(s) link. Pure parsing, no network: short links that only a
/// redirect can explain (fb.watch, facebook.com/share/…) are marked <see cref="NeedsResolve"/> and resolved later.
/// </summary>
public sealed partial record VideoLink(string Key, string Url, string OriginalUrl, WatchSource Source, WatchKind Kind, string? ExternalId, bool NeedsResolve)
{
    /// <summary>The first link in <paramref name="text"/> (with or without https://), or null when there is none.</summary>
    public static VideoLink? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match m in LinkPattern().Matches(text))
        {
            if (Parse(m.Value) is { } link) return link;
        }
        return null;
    }

    /// <summary>
    /// The text around the link in <paramref name="text"/>, trimmed (it becomes the note). An app that shares a title
    /// with the link ("Watch “…” on YouTube") gives that title here.
    /// </summary>
    public static string TextAround(string text, VideoLink link)
    {
        var at = text.IndexOf(link.OriginalUrl, StringComparison.Ordinal);
        var rest = at < 0 ? text : text.Remove(at, link.OriginalUrl.Length);
        return string.Join('\n', rest.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    /// <summary>Parses one link; null when it is not an http(s) link to a site.</summary>
    public static VideoLink? Parse(string raw)
    {
        var original = raw.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '"', '\'', '”', '’');
        var withScheme = original.Contains("://", StringComparison.Ordinal) ? original : "https://" + original;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;
        if (!uri.Host.Contains('.')) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = ParseQuery(uri.Query);

        return host switch
        {
            "youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtube-nocookie.com" or "youtu.be" => YouTube(original, host, segments, query),
            "facebook.com" or "m.facebook.com" or "web.facebook.com" or "mbasic.facebook.com" or "touch.facebook.com" or "fb.com" or "fb.watch" =>
                Facebook(original, host, segments, query, uri),
            _ => Other(original, uri),
        } ?? Other(original, uri);
    }

    private static VideoLink? YouTube(string original, string host, string[] segments, Dictionary<string, string> query)
    {
        string? id = null;
        var kind = WatchKind.Video;
        if (host == "youtu.be")
        {
            id = segments.FirstOrDefault();
        }
        else if (segments.Length >= 1 && segments[0] == "watch")
        {
            id = query.GetValueOrDefault("v");
        }
        else if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed" or "v" or "e")
        {
            id = segments[1];
            if (segments[0] == "shorts") kind = WatchKind.Short;
        }
        if (id is null || !YouTubeId().IsMatch(id)) return null;
        var url = kind == WatchKind.Short ? $"https://www.youtube.com/shorts/{id}" : $"https://www.youtube.com/watch?v={id}";
        return new VideoLink("youtube:" + id, url, original, WatchSource.YouTube, kind, id, NeedsResolve: false);
    }

    private static VideoLink? Facebook(string original, string host, string[] segments, Dictionary<string, string> query, Uri uri)
    {
        // fb.watch/abc and facebook.com/share/v/abc (video), /share/r/abc (reel), /share/abc: only the redirect knows.
        if (host == "fb.watch")
        {
            if (segments.Length == 0) return null;
            return Unresolved(original, $"https://fb.watch/{segments[0]}/", WatchKind.Video);
        }
        if (segments.Length >= 2 && segments[0] == "share")
        {
            var kind = segments[1] == "r" ? WatchKind.Short : WatchKind.Video;
            var path = string.Join('/', segments);
            return Unresolved(original, $"https://www.facebook.com/{path}/", kind);
        }

        string? id = null;
        var videoKind = WatchKind.Video;
        if (segments.Length >= 1 && segments[0] == "watch") id = query.GetValueOrDefault("v");
        else if (segments.Length >= 1 && segments[0] == "video.php") id = query.GetValueOrDefault("v");
        else if (segments.Length >= 2 && segments[0] is "reel" or "reels")
        {
            id = segments[1];
            videoKind = WatchKind.Short;
        }
        else
        {
            // /{page}/videos/{id}, /{page}/videos/{title-slug}/{id}, /watch/live/?v=
            var at = Array.IndexOf(segments, "videos");
            if (at >= 0) id = segments.Skip(at + 1).LastOrDefault(s => NumericId().IsMatch(s));
        }
        if (id is not null && NumericId().IsMatch(id))
        {
            var url = videoKind == WatchKind.Short ? $"https://www.facebook.com/reel/{id}" : $"https://www.facebook.com/watch/?v={id}";
            return new VideoLink("facebook:" + id, url, original, WatchSource.Facebook, videoKind, id, NeedsResolve: false);
        }

        // A post, a group permalink or a story with a video in it: keep the link itself (without tracking parameters).
        if (segments.Length == 0) return null;
        var clean = CleanUrl(new UriBuilder(uri) { Host = "www.facebook.com", Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri);
        return new VideoLink("facebook:" + clean, clean, original, WatchSource.Facebook, WatchKind.Video, null, NeedsResolve: false);
    }

    private static VideoLink Unresolved(string original, string url, WatchKind kind) =>
        new("facebook:" + url, url, original, WatchSource.Facebook, kind, null, NeedsResolve: true);

    private static VideoLink Other(string original, Uri uri)
    {
        var clean = CleanUrl(uri);
        return new VideoLink("url:" + clean, clean, original, WatchSource.Other, WatchKind.Video, null, NeedsResolve: false);
    }

    /// <summary>The link without its fragment and tracking parameters (utm_*, fbclid, si, …), host in lower case.</summary>
    internal static string CleanUrl(Uri uri)
    {
        var kept = ParseQueryPairs(uri.Query).Where(p => !IsTracking(p.Key)).Select(p => p.Raw).ToList();
        var builder = new UriBuilder(uri) { Host = uri.Host.ToLowerInvariant(), Fragment = "", Query = string.Join('&', kept) };
        if (builder.Uri.IsDefaultPort) builder.Port = -1;
        var text = builder.Uri.AbsoluteUri;
        return text.EndsWith('?') ? text[..^1] : text;
    }

    private static bool IsTracking(string key) =>
        key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("__", StringComparison.Ordinal)
        || key is "fbclid" or "gclid" or "si" or "feature" or "mibextid" or "rdid" or "ref" or "refsrc" or "sfnsn" or "s" or "igshid"
            or "share_url" or "app" or "pp" or "ab_channel" or "source" or "hc_ref" or "fs" or "notif_id" or "notif_t";

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value, _) in ParseQueryPairs(query)) result.TryAdd(key, value);
        return result;
    }

    private static IEnumerable<(string Key, string Value, string Raw)> ParseQueryPairs(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            yield return (key, value, part);
        }
    }

    // A URL with a scheme, or a bare link to one of the video sites.
    [GeneratedRegex(@"https?://[^\s<>""]+|(?<![\w.@/])(?:(?:www|m|web)\.)?(?:youtube\.com|youtu\.be|facebook\.com|fb\.watch)/[^\s<>""]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex YouTubeId();

    [GeneratedRegex("^[0-9]{5,25}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericId();
}
