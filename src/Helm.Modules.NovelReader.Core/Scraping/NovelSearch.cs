using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Scraping;

/// <summary>A novel found by name on a site: its title as the site shows it, its page, and the site.</summary>
public sealed record SearchHit(string Title, string Url, string Site);

/// <summary>
/// Picks the novels out of a site's search result page: links whose text holds the name searched for (compared in
/// Simplified, letters and digits only), not the search page itself, an author's page or a chapter ("最新更新 第12章…");
/// the exact title first, then titles starting with it, then the rest.
/// </summary>
public static class NovelSearch
{
    public const int MaxPerSite = 8;

    private static readonly string[] NotBooks = ["search", "/so/", "author", "/chapter/", "login", "register"];

    public static IReadOnlyList<SearchHit> Hits(string query, string pageUrl, IEnumerable<WebLink> links, string site)
    {
        var key = Key(query);
        if (key.Length == 0 || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return [];
        var hits = new List<(SearchHit Hit, int Rank)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            var text = link.Text.Trim();
            if (text.Length is 0 or > 40 || text.StartsWith("最新", StringComparison.Ordinal)) continue;
            if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            if (!SameSite(uri.Host, page.Host) || NotBooks.Any(n => uri.AbsolutePath.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
            var title = Key(text);
            if (!title.Contains(key, StringComparison.Ordinal)) continue;
            var url = uri.GetLeftPart(UriPartial.Query);
            if (!seen.Add(url)) continue;
            hits.Add((new SearchHit(text, url, site), title == key ? 0 : title.StartsWith(key, StringComparison.Ordinal) ? 1 : 2));
        }
        return hits.OrderBy(h => h.Rank).Take(MaxPerSite).Select(h => h.Hit).ToList();
    }

    /// <summary>An address that can be a novel's page: not a search page, an author's, a chapter, a login.</summary>
    public static bool IsBookPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !NotBooks.Any(n => uri.AbsolutePath.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string Key(string text) => BookUpdate.Key(TraditionalChinese.ToSimplified(text));

    private static bool SameSite(string a, string b) => string.Equals(Site(a), Site(b), StringComparison.OrdinalIgnoreCase);

    private static string Site(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..]
        : host.StartsWith("m.", StringComparison.OrdinalIgnoreCase) ? host[2..] : host;
}
