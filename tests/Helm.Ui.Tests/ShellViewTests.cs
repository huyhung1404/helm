using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Sync;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Shell.Tabs;
using Helm.Shell.Views;

namespace Helm.Ui.Tests;

/// <summary>The shared shell (Helm.Shell.UI ShellView: menu, tabs, search, title bar) around the Gallery's made-up Helm.</summary>
public sealed class ShellViewTests : IDisposable
{
    public void Dispose()
    {
        while (Sheet.CloseTopmost()) { }
    }

    private static async Task<(Window Window, ShellView View)> Show(ShellViewModel shell, double width = 1200, ShellView? view = null)
    {
        view ??= new ShellView(shell);
        var window = new Window { Content = view, Width = width, Height = 860 };
        window.Show();
        await Settle();
        return (window, view);
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 30; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private static List<T> Shown<T>(Control root) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible).ToList();

    private static List<string?> ShownTexts(Control root) => Shown<TextBlock>(root).Select(t => t.Text).ToList();

    private static void Press(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    private static T Part<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public async Task The_menu_lists_home_then_the_tools_by_group_then_bug_reports_and_general()
    {
        var shell = ShellDemo.Shell();
        var (window, view) = await Show(shell);

        Assert.Equal(["Planning", "Money & Media", "Security & Servers", "Windows & Desktop", "Learning", "Messages"], shell.Menu.Groups.Select(g => g.Title));
        Assert.Equal(["Habits", "Missions", "Notes", "Quick Capture", "Tracker"], shell.Menu.Groups[0].Items.Select(i => i.Title));
        Assert.Equal(["Home", "Habits"], shell.Menu.All.Take(2).Select(i => i.Title));
        Assert.Equal(["Bug reports", "General"], shell.Menu.All.TakeLast(2).Select(i => i.Title));
        var menu = Part<NavMenuView>(view, "Menu");
        var texts = ShownTexts(menu);
        Assert.Contains("PLANNING", texts);
        Assert.DoesNotContain("What's new", texts);
        Assert.DoesNotContain("Give feedback", texts);
        // A tool that is turned off is still listed, faded.
        Assert.True(shell.Menu.Groups.SelectMany(g => g.Items).Single(i => i.Key == "keyboard").IsOff);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_shown_tab_marks_its_menu_row_and_a_menu_row_opens_its_tab()
    {
        var shell = ShellDemo.Shell();
        var (window, view) = await Show(shell);
        Assert.True(shell.Menu.Home.IsActive);

        var wallet = shell.Menu.All.Single(i => i.Key == "wallet");
        shell.Menu.OpenCommand.Execute(wallet);
        await Settle();
        Assert.Equal("wallet", shell.Tabs.Main.Active?.Key);
        Assert.True(wallet.IsActive);
        Assert.False(shell.Menu.Home.IsActive);
        var rows = Shown<Button>(Part<NavMenuView>(view, "Menu")).Where(b => b.Classes.Contains("navrow") && b.Classes.Contains("active")).ToList();
        Assert.Equal(wallet, Assert.Single(rows).DataContext);

        // A page not open yet comes as a new tab at the end.
        shell.Menu.OpenCommand.Execute(shell.Menu.All.Single(i => i.Key == "vault"));
        Assert.Equal("vault", shell.Tabs.Main.Tabs[^1].Key);
        Assert.Same(shell.PageFor("vault"), view.ShownPage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Collapsing_the_menu_leaves_an_icon_rail_that_opens_over_the_page()
    {
        var shell = ShellDemo.Shell();
        var (window, view) = await Show(shell);
        var menu = Part<NavMenuView>(view, "Menu");
        Assert.Contains("Habits", ShownTexts(menu));

        shell.Menu.TogglePinnedCommand.Execute(null);
        await Settle();
        Assert.False(shell.Menu.IsOpen);
        Assert.Equal(NavMenuViewModel.RailWidth, shell.Menu.ReservedWidth);
        Assert.Contains("rail", menu.Classes);
        Assert.DoesNotContain("Habits", ShownTexts(menu));

        shell.Menu.IsPeeking = true;
        await Settle();
        Assert.True(shell.Menu.IsOverlay);
        Assert.Contains("Habits", ShownTexts(menu));
        // The page keeps its place: only the rail's room is reserved.
        Assert.Equal(NavMenuViewModel.RailWidth, shell.Menu.ReservedWidth);

        shell.Menu.TogglePinnedCommand.Execute(null);
        Assert.False(shell.Menu.IsPeeking);
        Assert.True(shell.Menu.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Tabs_open_once_close_reorder_and_the_main_window_never_stays_empty()
    {
        var shell = ShellDemo.Shell();
        var (window, view) = await Show(shell);
        var main = shell.Tabs.Main;
        Assert.Equal([ShellPages.Home, "notes", "wallet", "habits", "ssh"], main.Tabs.Select(t => t.Key));

        // Opening an open page goes to its tab.
        shell.Open("notes");
        Assert.Equal(5, main.Tabs.Count);
        Assert.Equal("notes", main.Active?.Key);
        Assert.Equal(5, Shown<Border>(Part<TabStripView>(view, "Strip")).Count(b => b.Classes.Contains("tab")));

        main.MoveTo(main.Tabs[1], 3);
        Assert.Equal([ShellPages.Home, "wallet", "habits", "notes", "ssh"], main.Tabs.Select(t => t.Key));
        Assert.Equal(["Home", "Wallet", "Habits", "Notes", "SSH"], shell.Main.Tabs.Select(t => t.Title));

        // Closing the shown tab shows the one after it; Ctrl+Tab wraps around.
        shell.Tabs.Close(main.Active!);
        Assert.Equal("ssh", main.Active?.Key);
        main.SelectNext(1);
        Assert.Equal(ShellPages.Home, main.Active?.Key);

        foreach (var tab in main.Tabs.ToList()) shell.Tabs.Close(tab);
        Assert.Equal([ShellPages.Home], main.Tabs.Select(t => t.Key));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ctrl_w_closes_the_shown_tab_and_ctrl_tab_moves_on()
    {
        var shell = ShellDemo.Shell();
        var (window, view) = await Show(shell);
        Press(view, Key.Tab, KeyModifiers.Control);
        await Settle();
        Assert.Equal("notes", shell.Tabs.Main.Active?.Key);
        Press(view, Key.W, KeyModifiers.Control);
        await Settle();
        Assert.Null(shell.Tabs.Find("notes"));
        Assert.Equal("wallet", shell.Tabs.Main.Active?.Key);
        window.Close();
    }

    [AvaloniaFact]
    public void Tabs_tear_off_into_a_window_of_their_own_and_come_back()
    {
        var shell = ShellDemo.Shell();
        var tabs = shell.Tabs;
        TabStrip? opened = null, closed = null;
        tabs.StripOpened += (_, s) => opened = s;
        tabs.StripClosed += (_, s) => closed = s;

        var strip = tabs.TearOff(tabs.Find("wallet")!);
        Assert.NotNull(strip);
        Assert.Same(strip, opened);
        Assert.Equal(["wallet"], strip!.Tabs.Select(t => t.Key));
        Assert.DoesNotContain(tabs.Main.Tabs, t => t.Key == "wallet");
        // A page is open once across windows: opening it goes to the torn-off window.
        Assert.Same(strip, tabs.Open("wallet").Strip);
        Assert.False(tabs.CanTearOff(tabs.Find("wallet")!));

        tabs.MoveToMain(tabs.Find("wallet")!);
        Assert.Same(strip, closed);
        Assert.Equal("wallet", tabs.Main.Tabs[^1].Key);
        Assert.Equal([tabs.Main], tabs.Strips);

        // The main window keeps one tab.
        while (tabs.Main.Tabs.Count > 1) tabs.Close(tabs.Main.Tabs[0]);
        Assert.Null(tabs.TearOff(tabs.Main.Tabs[0]));
    }

    [AvaloniaFact]
    public void The_main_windows_tabs_come_back_after_a_restart_unknown_pages_skipped()
    {
        var shell = ShellDemo.Shell();
        var saved = shell.Tabs.Save();
        Assert.Equal([ShellPages.Home, "notes", "wallet", "habits", "ssh"], saved.Keys);

        var again = new Helm.Shell.Views.ShellViewModel(new NoHost(), ShellDemo.Tools(), ShellDemo.Search(ShellDemo.Tools()));
        again.Restore(new TabsState(["wallet", "Helm.App.Views.Pages.WhatsNewPage", "notes"], "notes"));
        Assert.Equal(["wallet", "notes"], again.Tabs.Main.Tabs.Select(t => t.Key));
        Assert.Equal("notes", again.Tabs.Main.Active?.Key);

        var empty = new Helm.Shell.Views.ShellViewModel(new NoHost(), ShellDemo.Tools(), ShellDemo.Search(ShellDemo.Tools()));
        empty.Restore(null);
        Assert.Equal([ShellPages.Home], empty.Tabs.Main.Tabs.Select(t => t.Key));
    }

    [AvaloniaFact]
    public async Task Search_shows_results_under_the_box_and_enter_opens_the_highlighted_one()
    {
        var shell = ShellDemo.Shell();
        var (window, view) = await Show(shell);
        shell.Search.Query = "rem";
        await Settle();

        Assert.True(shell.Search.IsOpen);
        Assert.Equal(["Reminders", "Remind me about bills", "Remove old trash after", "Remember the keyboard per web site"],
            shell.Search.Results.Select(r => r.Title));
        Assert.True(shell.Search.Results[0].IsHighlighted);
        var results = Part<Border>(view, "Results");
        Assert.True(results.IsEffectivelyVisible);
        // It floats over the page: nothing between it and the window cuts it to the title bar's height.
        Assert.DoesNotContain(results.GetVisualAncestors().OfType<Control>(), c => c.ClipToBounds && c.Bounds.Height < 100);
        Assert.Contains("Reminders", ShownTexts(results));

        shell.Search.Move(1);
        var chosen = shell.Search.Highlighted!;
        Press(Part<TextBox>(view, "SearchBox"), Key.Enter);
        await Settle();
        Assert.False(shell.Search.IsOpen);
        Assert.Equal(chosen.Entry.PageKey, shell.Tabs.Main.Active?.Key);

        shell.Search.Query = "printer";
        await Settle();
        Assert.True(shell.Search.HasNoResults);
        Assert.Contains("No settings match “printer”", ShownTexts(view));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_narrow_window_makes_the_menu_a_drawer_and_the_tabs_a_count()
    {
        var shell = ShellDemo.Shell();
        var (window, view) = await Show(shell, width: 390);
        Assert.Contains("narrow", view.Classes);
        Assert.True(shell.Menu.IsDrawer);
        Assert.False(Part<TabStripView>(view, "Strip").IsEffectivelyVisible);
        Assert.False(Part<Border>(view, "SearchArea").IsEffectivelyVisible);
        Assert.Equal("5", Part<TextBlock>(view, "TabCount").Text);
        Assert.Equal("Home", Part<TextBlock>(view, "PageTitle").Text);
        Assert.False(shell.Menu.IsOpen);
        Assert.False(Part<Border>(view, "Scrim").IsVisible);

        shell.Menu.ToggleDrawerCommand.Execute(null);
        await Settle();
        Assert.True(shell.Menu.IsOpen);
        Assert.True(Part<Border>(view, "Scrim").IsVisible);
        Assert.Contains("Habits", ShownTexts(Part<NavMenuView>(view, "Menu")));

        shell.Menu.OpenCommand.Execute(shell.Menu.All.Single(i => i.Key == "wallet"));
        await Settle();
        Assert.False(shell.Menu.IsDrawerOpen);
        Assert.Equal("Wallet", Part<TextBlock>(view, "PageTitle").Text);

        window.Width = 1200;
        await Settle();
        Assert.False(shell.Menu.IsDrawer);
        Assert.True(Part<TabStripView>(view, "Strip").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_torn_off_window_shows_only_its_tabs()
    {
        var shell = ShellDemo.Shell();
        var strip = shell.Tabs.TearOff(shell.Tabs.Find("notes")!)!;
        var (window, view) = await Show(shell, view: new ShellView(shell, shell.StripFor(strip)));
        Assert.False(view.IsMainWindow);
        Assert.False(Part<NavMenuView>(view, "Menu").IsVisible);
        Assert.False(Part<Border>(view, "SearchArea").IsEffectivelyVisible);
        Assert.Same(shell.PageFor("notes"), view.ShownPage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_sync_glyph_follows_the_sync_state()
    {
        var (window, view) = await Show(ShellDemo.Shell(SyncState.Error));
        var glyph = Part<FluentIcons.Avalonia.SymbolIcon>(view, "SyncGlyph");
        Assert.Contains("problem", glyph.Classes);
        window.Close();

        (window, view) = await Show(ShellDemo.Shell(SyncState.Offline));
        Assert.Contains("muted", Part<FluentIcons.Avalonia.SymbolIcon>(view, "SyncGlyph").Classes);
        window.Close();

        (window, view) = await Show(ShellDemo.Shell(SyncState.Syncing));
        Assert.Contains("turning", Part<FluentIcons.Avalonia.SymbolIcon>(view, "SyncGlyph").Classes);
        window.Close();
    }

    private sealed class NoHost : IShellHost
    {
        public Control CreatePage(string key) => new TextBlock { Text = key };

        public void Reveal(string key, Control page, string anchor) { }

        public void ReportBug() { }

        public void OpenWindow(TabStrip strip) { }

        public void CloseWindow(TabStrip strip) { }

        public void ShowWindow(TabStrip strip) { }
    }
}
