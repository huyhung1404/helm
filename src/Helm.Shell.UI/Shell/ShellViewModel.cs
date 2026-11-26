using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Shell.Search;
using Helm.Shell.Tabs;
using Helm.Shell.ViewModels;

namespace Helm.Shell.Views;

/// <summary>What the shell asks of the app it runs in: the pages' views, the bug report, windows for torn-off tabs.</summary>
public interface IShellHost
{
    /// <summary>The view of a page (<see cref="ShellPages"/> or a tool's id); asked once per page for the app's life.</summary>
    Control CreatePage(string key);

    /// <summary>A search result inside a page: the page scrolls to that part or opens it (General's section, a card).</summary>
    void Reveal(string key, Control page, string anchor);

    /// <summary>The title bar's bug button: freezes the black box and opens the report.</summary>
    void ReportBug();

    /// <summary>A tab was torn off onto <paramref name="strip"/>: open a window for it (a <see cref="ShellView"/> in tab-window mode).</summary>
    void OpenWindow(TabStrip strip);

    /// <summary>A window's last tab left it: close that window.</summary>
    void CloseWindow(TabStrip strip);

    /// <summary>A tab was chosen from outside its window: bring that window to the front.</summary>
    void ShowWindow(TabStrip strip);
}

/// <summary>
/// The whole shell around the pages, one for the app: the menu, the tabs of every window, the search, the sync button
/// and the bug button. Each window's <see cref="ShellView"/> binds to it with its own <see cref="TabStripViewModel"/>
/// (<see cref="Main"/> for the main window, <see cref="StripFor"/> for a torn-off one).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IShellHost _host;
    private readonly Dictionary<string, Control> _pages = [];
    private readonly Dictionary<TabStrip, TabStripViewModel> _strips = [];

    public ShellViewModel(IShellHost host, IEnumerable<ShellToolItem> tools, SettingsSearch search, SyncIndicatorViewModel? sync = null)
    {
        _host = host;
        Catalog = new ShellCatalog(tools);
        Tabs = new TabSet(Catalog.TitleOf);
        Menu = new NavMenuViewModel(Catalog, key => Open(key));
        Search = new SearchViewModel(search, Catalog, Navigate);
        Sync = sync;
        Main = StripFor(Tabs.Main);
        Tabs.Main.Changed += (_, _) => Menu.MarkActive(Tabs.Main.Active?.Key);
        Tabs.StripOpened += (_, strip) => host.OpenWindow(strip);
        Tabs.StripClosed += (_, strip) =>
        {
            _strips.Remove(strip);
            host.CloseWindow(strip);
        };
        Tabs.ShowRequested += (_, tab) =>
        {
            if (tab.Strip is { } strip) host.ShowWindow(strip);
        };
    }

    public ShellCatalog Catalog { get; }

    public TabSet Tabs { get; }

    public NavMenuViewModel Menu { get; }

    public SearchViewModel Search { get; }

    /// <summary>The sync button; null hides it (a build without sync).</summary>
    public SyncIndicatorViewModel? Sync { get; }

    /// <summary>The main window's tabs.</summary>
    public TabStripViewModel Main { get; }

    public string AppName { get; init; } = "Helm";

    /// <summary>The tab strip of a window: the main one, or a torn-off one (made on first use).</summary>
    public TabStripViewModel StripFor(TabStrip strip)
    {
        if (!_strips.TryGetValue(strip, out var model)) model = _strips[strip] = new TabStripViewModel(strip, Catalog, PageFor);
        return model;
    }

    /// <summary>The page's view, made once and kept (a tab that moves or opens again shows the same view).</summary>
    public Control PageFor(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) page = _pages[key] = _host.CreatePage(key);
        return page;
    }

    /// <summary>Shows a page in the window used last (the menu: the main window).</summary>
    public ShellTab Open(string key, TabStrip? target = null) => Tabs.Open(key, target ?? Tabs.Main);

    /// <summary>Restores the main window's tabs (or opens <paramref name="start"/>, or Home).</summary>
    public void Restore(TabsState? saved, string? start = null) => Tabs.Restore(saved, Catalog.Knows, start);

    private void Navigate(SearchEntry entry)
    {
        if (!Catalog.Knows(entry.PageKey)) return;
        Tabs.Open(entry.PageKey);
        if (entry.Anchor is { } anchor) _host.Reveal(entry.PageKey, PageFor(entry.PageKey), anchor);
    }

    [RelayCommand]
    private void ReportBug() => _host.ReportBug();
}
