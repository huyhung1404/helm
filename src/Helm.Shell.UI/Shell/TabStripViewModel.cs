using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Shell.Tabs;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Shell.Views;

/// <summary>A tab on the strip: the page's icon and title, and its close button.</summary>
public sealed partial class TabItemViewModel : ObservableObject
{
    private readonly TabStripViewModel _strip;

    internal TabItemViewModel(ShellTab tab, ShellPageInfo? page, TabStripViewModel strip)
    {
        Tab = tab;
        _strip = strip;
        Icon = page?.Icon;
        Glyph = page?.Glyph ?? Symbol.Document;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellTab.IsActive)) OnPropertyChanged(nameof(IsActive));
            if (e.PropertyName == nameof(ShellTab.Title)) OnPropertyChanged(nameof(Title));
        };
    }

    public ShellTab Tab { get; }

    public string Title => Tab.Title;

    public IImage? Icon { get; }

    public Symbol Glyph { get; }

    public bool IsActive => Tab.IsActive;

    public string CloseName => $"Close {Title}";

    [RelayCommand]
    private void Close() => _strip.Set.Close(Tab);

    [RelayCommand]
    private void Show() => _strip.Strip.Activate(Tab);

    [RelayCommand]
    private void MoveToNewWindow() => _strip.Set.TearOff(Tab);

    [RelayCommand]
    private void MoveToMainWindow() => _strip.Set.MoveToMain(Tab);

    [RelayCommand]
    private void CloseOthers() => _strip.Set.CloseOthers(Tab);

    /// <summary>The only tab of a window is a window of its own already; the main window keeps one tab.</summary>
    public bool CanMoveToNewWindow => _strip.Set.CanTearOff(Tab);

    public bool CanMoveToMainWindow => !_strip.Strip.IsMain;

    public bool CanCloseOthers => _strip.Strip.Tabs.Count > 1;
}

/// <summary>
/// One window's tab strip as the view shows it: the tabs (in step with its <see cref="TabStrip"/>), the "+" list and
/// the shown tab's page. Each page's view is made once (<see cref="ShellViewModel.PageFor"/>) and kept while its tab moves between
/// windows, so it keeps its scroll position, an open item, a terminal.
/// </summary>
public sealed partial class TabStripViewModel : ObservableObject
{
    private readonly ShellCatalog _catalog;
    private readonly Func<string, Control> _page;
    private readonly Dictionary<ShellTab, TabItemViewModel> _items = [];

    public TabStripViewModel(TabStrip strip, ShellCatalog catalog, Func<string, Control> page)
    {
        Strip = strip;
        _catalog = catalog;
        _page = page;
        strip.Changed += (_, _) => Sync();
        Sync();
    }

    public TabStrip Strip { get; }

    public TabSet Set => Strip.Owner;

    public ObservableCollection<TabItemViewModel> Tabs { get; } = [];

    /// <summary>The "+" list: every page a tab can open, Home first.</summary>
    public IReadOnlyList<ShellPageInfo> Launchable => _catalog.Launchable;

    /// <summary>The shown tab's page.</summary>
    [ObservableProperty]
    private Control? _activePage;

    [ObservableProperty]
    private string? _activeTitle;

    public int Count => Tabs.Count;

    /// <summary>Opens (or goes to) a page in this window: the "+" list.</summary>
    [RelayCommand]
    private void Launch(ShellPageInfo? page)
    {
        if (page is not null) Set.Open(page.Key, Strip);
    }

    private void Sync()
    {
        var wanted = Strip.Tabs.Select(Item).ToList();
        for (var i = Tabs.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Tabs[i])) Tabs.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = Tabs.IndexOf(wanted[i]);
            if (at < 0) Tabs.Insert(i, wanted[i]);
            else if (at != i) Tabs.Move(at, i);
        }
        foreach (var gone in _items.Keys.Where(t => !ReferenceEquals(t.Strip, Strip)).ToList()) _items.Remove(gone);
        ActivePage = Strip.Active is { } active ? _page(active.Key) : null;
        ActiveTitle = Strip.Active?.Title;
        OnPropertyChanged(nameof(Count));
    }

    private TabItemViewModel Item(ShellTab tab)
    {
        if (!_items.TryGetValue(tab, out var item)) item = _items[tab] = new TabItemViewModel(tab, _catalog.Find(tab.Key), this);
        return item;
    }
}
