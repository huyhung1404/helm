namespace Helm.Shell.Search;

/// <summary>
/// One thing the title bar's search finds: a page (Home, General, a tool) or a part of one (a settings card, a General
/// section) that <see cref="Anchor"/> names, so the page can scroll to it or open it.
/// </summary>
public sealed record SearchEntry(string Title, string Location, string PageKey, string? Anchor = null, int Rank = 0)
{
    public bool IsPage => Anchor is null;
}

/// <summary>What a page offers the search (its settings cards' titles). Asked again after <see cref="SettingsSearch.Invalidate"/>.</summary>
public interface ISearchSource
{
    IEnumerable<SearchEntry> Entries();
}

/// <summary>
/// "Search for settings": every term must match an entry's title or location; a title that starts with a term scores
/// most, then one that contains it, then the location; pages win ties, then the order they were given in.
/// </summary>
public sealed class SettingsSearch(IEnumerable<ISearchSource> sources)
{
    private List<SearchEntry>? _index;

    public SettingsSearch(params SearchEntry[] entries) : this([new FixedSource(entries)])
    {
    }

    /// <summary>Tools turned on or off, a page's cards changed: the next search builds the index again.</summary>
    public void Invalidate() => _index = null;

    public IReadOnlyList<SearchEntry> Search(string? query, int max = 12)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        _index ??= sources.SelectMany(s => s.Entries()).ToList();
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return _index
            .Select((entry, order) => (entry, order, score: Score(entry, terms)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.entry.Rank)
            .ThenBy(x => x.order)
            .Select(x => x.entry)
            .Take(max)
            .ToList();
    }

    private static int Score(SearchEntry entry, string[] terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            if (entry.Title.StartsWith(term, StringComparison.CurrentCultureIgnoreCase)) score += 4;
            else if (entry.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)) score += 2;
            else if (entry.Location.Contains(term, StringComparison.CurrentCultureIgnoreCase)) score += 1;
            else return 0;
        }
        return entry.IsPage ? score + 1 : score;
    }

    private sealed class FixedSource(IReadOnlyList<SearchEntry> entries) : ISearchSource
    {
        public IEnumerable<SearchEntry> Entries() => entries;
    }
}
