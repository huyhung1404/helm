using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Shell.Search;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Shell.Views;

/// <summary>A row of the search results: the page's icon, the title, and where it is.</summary>
public sealed partial class SearchResultViewModel(SearchEntry entry, ShellPageInfo? page) : ObservableObject
{
    public SearchEntry Entry { get; } = entry;

    public string Title => Entry.Title;

    /// <summary>"Wallet · Reminders" for a part of a page; the group ("Planning") for a page.</summary>
    public string Location => Entry.Location;

    public IImage? Icon => page?.Icon;

    public Symbol Glyph => page?.Glyph ?? Symbol.Settings;

    /// <summary>The row the arrow keys are on; Enter opens it.</summary>
    [ObservableProperty]
    private bool _isHighlighted;
}

/// <summary>
/// The title bar's "Search for settings": results update as you type, in a floating list under the box; arrow keys move
/// through them, Enter opens the highlighted one (its page, scrolled to the part found), Escape closes the list.
/// </summary>
public sealed partial class SearchViewModel(SettingsSearch search, ShellCatalog catalog, Action<SearchEntry> open) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private string _query = "";

    /// <summary>The floating list is shown (something was typed and the box has the focus).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private bool _isOpen;

    public ObservableCollection<SearchResultViewModel> Results { get; } = [];

    /// <summary>Something was typed and nothing matches: the list says so instead of vanishing.</summary>
    public bool HasNoResults => IsOpen && Results.Count == 0 && !string.IsNullOrWhiteSpace(Query);

    public string NoResultsText => $"No settings match “{Query.Trim()}”";

    public SearchResultViewModel? Highlighted => Results.FirstOrDefault(r => r.IsHighlighted);

    partial void OnQueryChanged(string value)
    {
        Results.Clear();
        foreach (var entry in search.Search(value)) Results.Add(new SearchResultViewModel(entry, catalog.Find(entry.PageKey)));
        if (Results.Count > 0) Results[0].IsHighlighted = true;
        IsOpen = !string.IsNullOrWhiteSpace(value);
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(HasNoResults));
    }

    /// <summary>Down (+1) / Up (−1) through the results, wrapping around.</summary>
    public void Move(int step)
    {
        if (Results.Count == 0) return;
        var index = Results.IndexOf(Highlighted!);
        var next = ((index < 0 ? (step > 0 ? -1 : 0) : index) + step + Results.Count) % Results.Count;
        for (var i = 0; i < Results.Count; i++) Results[i].IsHighlighted = i == next;
    }

    [RelayCommand]
    private void Choose(SearchResultViewModel? result)
    {
        result ??= Highlighted;
        if (result is null) return;
        Close();
        Query = "";
        IsOpen = false;
        open(result.Entry);
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>The box got the focus again with text in it: show its results again.</summary>
    public void Reopen()
    {
        if (!string.IsNullOrWhiteSpace(Query)) IsOpen = true;
    }
}
