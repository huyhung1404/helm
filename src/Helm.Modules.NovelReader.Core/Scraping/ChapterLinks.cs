using System.Text.RegularExpressions;
using Helm.Modules.NovelReader.Books;

namespace Helm.Modules.NovelReader.Scraping;

/// <summary>A link on a web page: its text and its absolute address.</summary>
public sealed record WebLink(string Text, string Url);

/// <summary>
/// Finds a novel's chapters among every link of its chapter-list page. Chapters are the biggest group of links in one
/// folder of the same site (/html/15/15400/123.html, /read/38804/p1.html, /chapter/1010868264/402733549/); a folder that
/// carries the book's own id counts ten times, so a sidebar of other books never wins. A chapter listed twice (the
/// "latest chapters" block above the full list) keeps its place in the full list, and a list written newest first is
/// turned around.
/// </summary>
public static partial class ChapterLinks
{
    [GeneratedRegex(@"^\s*第?\s*\d+\s*[-~～—–至到]\s*\d+\s*[章节回]?\s*$")]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"^\s*第\s*\d+\s*页\s*$|^\s*\d+\s*/\s*\d+\s*$")]
    private static partial Regex PageNumberPattern();

    [GeneratedRegex(@"下一页|下一頁|下页|下頁")]
    private static partial Regex NextPagePattern();

    [GeneratedRegex(@"\d{3,}")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"(?:https?://)?(?:[a-z0-9-]+\.)*[a-z0-9-]*\.(?:com|net|org|cc|tw|cn|info|me|co|xyz|top|vip)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Domain();

    /// <summary>The chapters, in reading order, from the links of the chapter list (all its pages, in order).</summary>
    public static IReadOnlyList<WebLink> Pick(string tocUrl, IEnumerable<WebLink> links)
    {
        if (!Uri.TryCreate(tocUrl, UriKind.Absolute, out var toc)) return [];
        var tocIds = IdPattern().Matches(toc.AbsolutePath).Select(m => m.Value).ToHashSet();
        // /book/7848572/ is the book's own folder; /html/15/15400/index.html sits in it.
        var tocFolder = toc.AbsolutePath.EndsWith('/') ? Stem(toc) : Folder(toc);
        var tocStem = Stem(toc);

        var groups = new Dictionary<string, List<(int Index, WebLink Link)>>(StringComparer.Ordinal);
        var index = 0;
        foreach (var link in links)
        {
            index++;
            if (IsListPage(link.Text) || !TryNormalize(link.Url, out var uri) || !SameSite(uri, toc)) continue;
            if (Same(uri, toc)) continue;
            var key = Folder(uri);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
            group.Add((index, link with { Text = CleanText(link.Text), Url = uri.AbsoluteUri }));
        }

        var best = groups
            .Select(g => (g.Key, Links: g.Value, Score: Score(g.Key, g.Value)))
            .OrderByDescending(g => g.Score)
            .FirstOrDefault();
        if (best.Links is null) return [];

        double Score(string key, List<(int, WebLink Link)> group)
        {
            var urls = group.Select(l => l.Link.Url).Distinct().Count();
            var headings = group.Count(l => ChapterSplitter.LooksLikeHeading(l.Link.Text));
            var own = key == tocStem || key == tocFolder.TrimEnd('/') || IdPattern().Matches(key).Any(m => tocIds.Contains(m.Value));
            return (urls + 2.0 * headings) * (own ? 10 : 1);
        }

        // The last time a chapter is listed is its place in the full list; a better text from an earlier listing is kept.
        var last = new Dictionary<string, (int Index, WebLink Link)>(StringComparer.Ordinal);
        foreach (var (i, link) in best.Links)
        {
            var text = link.Text.Length > 0 && !LooksLikeUrl(link.Text) ? link.Text
                : last.TryGetValue(link.Url, out var earlier) ? earlier.Link.Text : link.Text;
            last[link.Url] = (i, link with { Text = text });
        }
        var chapters = last.Values.OrderBy(l => l.Index).Select(l => l.Link).ToList();

        if (MostlyDescending(chapters)) chapters.Reverse();
        return chapters;
    }

    /// <summary>
    /// Other pages of the chapter list linked from this one: "下一页", "第2页", "101-200章" (as links or in a page picker).
    /// </summary>
    public static IReadOnlyList<string> ListPages(string pageUrl, IEnumerable<WebLink> links)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return [];
        // A page of this book's list carries the book's id (/book/123/index_2.html, ?id=123&page=2).
        var ids = IdPattern().Matches(page.PathAndQuery).Select(m => m.Value).ToList();
        var pages = new List<string>();
        foreach (var link in links)
        {
            if (!IsListPage(link.Text) || !TryNormalize(link.Url, out var uri) || !SameSite(uri, page) || Same(uri, page)) continue;
            if (ids.Count > 0 && !ids.Any(id => uri.PathAndQuery.Contains(id, StringComparison.Ordinal))) continue;
            if (!pages.Contains(uri.AbsoluteUri)) pages.Add(uri.AbsoluteUri);
        }
        return pages;
    }

    /// <summary>
    /// The next page of the same chapter, or null: "下一页" to an address that carries the chapter's own name with a page
    /// added (123.html → 123_2.html, 123-2.html, 123/2.html) and is not another chapter of the list.
    /// </summary>
    public static string? Continuation(string pageUrl, string chapterUrl, IEnumerable<WebLink> nextLinks, IReadOnlySet<string> chapterUrls, IReadOnlySet<string> seen)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || !Uri.TryCreate(chapterUrl, UriKind.Absolute, out var chapter)) return null;
        var stem = Stem(chapter);
        foreach (var link in nextLinks)
        {
            if (!NextPagePattern().IsMatch(link.Text) || !TryNormalize(link.Url, out var uri) || !SameSite(uri, page)) continue;
            var url = uri.AbsoluteUri;
            if (Same(uri, page) || chapterUrls.Contains(url) || seen.Contains(url)) continue;
            var next = Stem(uri);
            // 1234.html is another chapter, not a page of 123.html.
            if (next.Length > stem.Length && next.StartsWith(stem, StringComparison.Ordinal) && !char.IsDigit(next[stem.Length])) return url;
            if (next == stem && uri.Query.Length > 0) return url;
        }
        return null;
    }

    /// <summary>A chapter's link text without the site's address some sites slip into it ("第 53 章.com", ".com現實，千露？").</summary>
    public static string CleanText(string text) => string.Join(' ', Domain().Replace(text, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A link to another page of the list rather than to a chapter.</summary>
    public static bool IsListPage(string text) =>
        RangePattern().IsMatch(text) || PageNumberPattern().IsMatch(text) || NextPagePattern().IsMatch(text.Trim()) && text.Trim().Length <= 8;

    private static bool TryNormalize(string url, out Uri uri)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri!) || uri.Scheme is not ("http" or "https")) return false;
        if (uri.Fragment.Length > 0) uri = new Uri(uri.GetLeftPart(UriPartial.Query));
        return true;
    }

    /// <summary>Same site, with or without www/m in front (www.piaotia.com, piaotia.com).</summary>
    private static bool SameSite(Uri a, Uri b) => string.Equals(Site(a.Host), Site(b.Host), StringComparison.OrdinalIgnoreCase);

    private static string Site(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..]
        : host.StartsWith("m.", StringComparison.OrdinalIgnoreCase) ? host[2..] : host;

    private static bool Same(Uri a, Uri b) =>
        SameSite(a, b) && string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) && a.Query == b.Query;

    /// <summary>The address without its last part: /html/15/15400/123.html → /html/15/15400, /chapter/1/2/ → /chapter/1.</summary>
    private static string Folder(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', segments.Take(Math.Max(0, segments.Length - 1))).ToLowerInvariant();
    }

    /// <summary>The address without its extension: /books/396941.html → /books/396941.</summary>
    private static string Stem(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        var dot = path.LastIndexOf('.');
        return dot > path.LastIndexOf('/') ? path[..dot] : path;
    }

    private static bool LooksLikeUrl(string text) => text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Chapter numbers in the addresses mostly go down: the site lists the newest first.</summary>
    private static bool MostlyDescending(List<WebLink> chapters)
    {
        long? previous = null;
        int up = 0, down = 0;
        foreach (var chapter in chapters)
        {
            var id = Uri.TryCreate(chapter.Url, UriKind.Absolute, out var uri) ? LastNumber(uri.AbsolutePath) : null;
            if (id is null) continue;
            if (previous is { } p)
            {
                if (id > p) up++;
                else if (id < p) down++;
            }
            previous = id;
        }
        return down > 3 * Math.Max(up, 1);
    }

    private static long? LastNumber(string path)
    {
        var matches = Digits().Matches(path);
        return matches.Count > 0 && long.TryParse(matches[^1].Value, out var n) ? n : null;
    }
}
