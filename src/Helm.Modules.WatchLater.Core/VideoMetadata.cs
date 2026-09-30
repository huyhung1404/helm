using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.WatchLater;

/// <summary>What was found about a video; any field may be missing.</summary>
/// <param name="ResolvedUrl">Where a short link (fb.watch, facebook.com/share/…) leads.</param>
public sealed record VideoMetadata(string? Title, string? Channel, string? ThumbnailUrl, int? DurationSeconds, string? ResolvedUrl = null)
{
    public static VideoMetadata Empty { get; } = new(null, null, null, null);

    /// <summary>Everything the card shows is known.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(ThumbnailUrl) && DurationSeconds is > 0;

    /// <summary>This, with the gaps filled from <paramref name="other"/>.</summary>
    public VideoMetadata Merge(VideoMetadata other) => new(
        string.IsNullOrWhiteSpace(Title) ? other.Title : Title,
        string.IsNullOrWhiteSpace(Channel) ? other.Channel : Channel,
        string.IsNullOrWhiteSpace(ThumbnailUrl) ? other.ThumbnailUrl : ThumbnailUrl,
        DurationSeconds is > 0 ? DurationSeconds : other.DurationSeconds,
        ResolvedUrl ?? other.ResolvedUrl);
}

/// <summary>Looks up a video's title, channel, thumbnail and length.</summary>
public interface IVideoMetadataSource
{
    /// <summary>Lower runs first (yt-dlp on Windows before the web pages).</summary>
    int Order { get; }

    /// <summary>Null when this source knows nothing; throws only on cancellation.</summary>
    Task<VideoMetadata?> FetchAsync(WatchItem item, CancellationToken ct);
}

/// <summary>
/// Metadata from the web, on both platforms, without any account: YouTube's oEmbed and watch page (for the length),
/// and the Open Graph tags Facebook serves to link previews. Short links are followed to find the real video.
/// </summary>
public sealed partial class HttpVideoMetadataSource : IVideoMetadataSource, IDisposable
{
    private const int MaxPageBytes = 3 * 1024 * 1024;

    // Facebook serves its link-preview tags to its own crawler; a normal browser gets a sign-in page.
    private const string PreviewAgent = "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)";
    private const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public HttpVideoMetadataSource(ILogger<HttpVideoMetadataSource>? logger = null, HttpMessageHandler? handler = null)
    {
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10, AutomaticDecompression = DecompressionMethods.All })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public int Order => 100;

    public async Task<VideoMetadata?> FetchAsync(WatchItem item, CancellationToken ct)
    {
        try
        {
            return item.Source switch
            {
                WatchSource.YouTube when item.ExternalId is { } id => await YouTubeAsync(id, ct).ConfigureAwait(false),
                WatchSource.Facebook => await FacebookAsync(item.Url, ct).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, blocked, a changed page: the card shows the link, and it is tried again later.
            _logger.LogInformation("Looking up {Source} video failed: {Message}", item.Source, ex.Message);
            return null;
        }
    }

    private async Task<VideoMetadata> YouTubeAsync(string id, CancellationToken ct)
    {
        var watch = $"https://www.youtube.com/watch?v={id}";
        string? title = null, channel = null;
        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(watch)))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                title = Str(json.RootElement, "title");
                channel = Str(json.RootElement, "author_name");
            }
        }
        // The length is only on the watch page.
        int? duration = null;
        try
        {
            var (page, _) = await GetPageAsync(watch, BrowserAgent, ct).ConfigureAwait(false);
            duration = YouTubeDuration(page);
            if (title is null)
            {
                var tags = MetaTags(page);
                title = tags.GetValueOrDefault("og:title");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("YouTube watch page failed: {Message}", ex.Message);
        }
        return new VideoMetadata(title, channel, WatchLaterStore.YouTubeThumbnail(id), duration);
    }

    private async Task<VideoMetadata> FacebookAsync(string url, CancellationToken ct)
    {
        var (page, finalUrl) = await GetPageAsync(url, PreviewAgent, ct).ConfigureAwait(false);
        var tags = MetaTags(page);
        var (title, channel) = CleanFacebookTitle(tags.GetValueOrDefault("og:title") ?? tags.GetValueOrDefault("twitter:title"));
        var duration = int.TryParse(tags.GetValueOrDefault("video:duration") ?? tags.GetValueOrDefault("og:video:duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : (int?)null;
        var image = tags.GetValueOrDefault("og:image") ?? tags.GetValueOrDefault("twitter:image");
        var resolved = tags.GetValueOrDefault("og:url") is { Length: > 0 } og ? og : finalUrl;
        return new VideoMetadata(title, channel, image, duration, resolved);
    }

    private async Task<(string Page, string FinalUrl)> GetPageAsync(string url, string agent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", agent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8,vi;q=0.6");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        // Skips YouTube's cookie consent page in Europe.
        request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI; CONSENT=YES+");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxPageBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0) total += read;
        var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
        return (Encoding.UTF8.GetString(buffer, 0, total), finalUrl);
    }

    // ---- Parsing (internal for tests) ----------------------------------------------------------------------------

    /// <summary>The page's &lt;meta property/name="…" content="…"&gt; tags, first one wins, HTML entities decoded.</summary>
    internal static Dictionary<string, string> MetaTags(string html)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match meta in MetaTag().Matches(html))
        {
            string? name = null, content = null;
            foreach (Match attr in Attribute().Matches(meta.Value))
            {
                var key = attr.Groups[1].Value.ToLowerInvariant();
                var value = attr.Groups[3].Success ? attr.Groups[3].Value : attr.Groups[4].Value;
                if (key is "property" or "name" or "itemprop") name ??= value;
                else if (key == "content") content = value;
            }
            if (name is not null && content is not null) tags.TryAdd(name, WebUtility.HtmlDecode(content).Trim());
        }
        return tags;
    }

    /// <summary>The length on a YouTube watch page ("lengthSeconds", else the itemprop duration "PT4M13S").</summary>
    internal static int? YouTubeDuration(string page)
    {
        if (LengthSeconds().Match(page) is { Success: true } m && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0) return s;
        if (MetaTags(page).GetValueOrDefault("duration") is { } iso && IsoDuration().Match(iso) is { Success: true } d)
        {
            int Part(int g) => d.Groups[g].Success ? int.Parse(d.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
            var total = Part(1) * 3600 + Part(2) * 60 + Part(3);
            return total > 0 ? total : null;
        }
        return null;
    }

    /// <summary>
    /// Facebook preview titles look like "1.2K views · 45 reactions | The caption | Page name" or "Caption | By Page |
    /// Facebook": this keeps the caption and the page.
    /// </summary>
    internal static (string? Title, string? Channel) CleanFacebookTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return (null, null);
        var parts = title.Split(" | ").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count > 0 && parts[^1].Equals("Facebook", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(parts.Count - 1);
        if (parts.Count > 0 && ViewsPrefix().IsMatch(parts[0])) parts.RemoveAt(0);
        string? channel = null;
        var by = parts.FindIndex(p => p.StartsWith("By ", StringComparison.Ordinal));
        if (by >= 0)
        {
            channel = parts[by][3..].Trim();
            parts.RemoveAt(by);
        }
        else if (parts.Count >= 2)
        {
            channel = parts[^1];
            parts.RemoveAt(parts.Count - 1);
        }
        var text = string.Join(" | ", parts).Trim();
        if (text.Length == 0) (text, channel) = (channel ?? "", null);
        return (text.Length == 0 ? null : WatchLaterFormat.Shorten(text, 200), string.IsNullOrWhiteSpace(channel) ? null : channel);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public void Dispose() => _http.Dispose();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"([a-zA-Z:_-]+)\s*=\s*(""([^""]*)""|'([^']*)')", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"""lengthSeconds""\s*:\s*""(\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex LengthSeconds();

    [GeneratedRegex(@"^PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDuration();

    // "1.2K views · 45 reactions", "2,8 triệu lượt xem · 1,2K cảm xúc": the counts Facebook puts first.
    [GeneratedRegex(@"^\d.*\b(views?|reactions?|likes?|comments?|lượt xem|cảm xúc|lượt thích|bình luận)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ViewsPrefix();
}
