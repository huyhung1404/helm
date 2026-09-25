using System.Windows;
using System.Windows.Controls;
using Helm.App.Views.Pages;
using Helm.Core.Modules;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Helm.App.Services;

/// <summary>One searchable thing: a page (nav item) or a settings card on a page.</summary>
internal sealed record SearchResult(string Title, string Location, Type PageType, FrameworkElement? Target, int Rank)
{
    public override string ToString() => Target is null ? Title : $"{Title}  ·  {Location}";
}

/// <summary>
/// Indexes nav items and every <see cref="CardHeader"/> on the shell and module pages, so the title-bar search box
/// can filter them and jump straight to a card.
/// </summary>
internal sealed class SearchService(IServiceProvider services, IModuleHost modules)
{
    private List<SearchResult>? _index;

    public void Invalidate() => _index = null;

    public IReadOnlyList<SearchResult> Search(string query, int max = 12)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        _index ??= BuildIndex();

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return _index
            .Select(r => (r, score: Score(r, terms)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.r.Rank)
            .Select(x => x.r)
            .Take(max)
            .ToList();
    }

    private static int Score(SearchResult r, string[] terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            if (r.Title.StartsWith(term, StringComparison.CurrentCultureIgnoreCase)) score += 4;
            else if (r.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)) score += 2;
            else if (r.Location.Contains(term, StringComparison.CurrentCultureIgnoreCase)) score += 1;
            else return 0; // every term must match somewhere
        }
        return r.Target is null ? score + 1 : score; // pages win ties
    }

    private List<SearchResult> BuildIndex()
    {
        var results = new List<SearchResult>
        {
            new("Home", "Helm", typeof(HomePage), null, 0),
            new("General", "Helm", typeof(GeneralPage), null, 1),
        };
        AddCards(results, typeof(GeneralPage), "General");

        foreach (var module in modules.Modules)
        {
            results.Add(new SearchResult(module.DisplayName, module.Group.DisplayName(), module.SettingsPageType, null, 2));
            AddCards(results, module.SettingsPageType, module.DisplayName);
        }
        return results;
    }

    private void AddCards(List<SearchResult> results, Type pageType, string location)
    {
        if (services.GetService(pageType) is not DependencyObject page) return;
        var rank = 10;
        foreach (var (header, card) in FindHeaders(page, null))
        {
            if (string.IsNullOrWhiteSpace(header.Title)) continue;
            results.Add(new SearchResult(header.Title, location, pageType, card, rank++));
        }
    }

    private static IEnumerable<(CardHeader Header, FrameworkElement Card)> FindHeaders(DependencyObject node, FrameworkElement? card)
    {
        if (node is CardControl or CardExpander) card = (FrameworkElement)node;

        if (node is CardHeader header)
        {
            yield return (header, card ?? header);
            yield break;
        }

        var children = LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToList();
        // Headers of cards are not always logical children; include them explicitly.
        if (node is CardControl { Header: DependencyObject h1 }) children.Add(h1);
        if (node is HeaderedContentControl { Header: DependencyObject h2 } && !children.Contains(h2)) children.Add(h2);
        if (node is ModulePageBase { Body: DependencyObject body } && !children.Contains(body)) children.Add(body);

        foreach (var child in children)
            foreach (var found in FindHeaders(child, card))
                yield return found;
    }
}
