using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Helm.Core.Modules;
using Helm.Core.Sync;
using Helm.Core.Ui;
using Helm.Shell.Search;
using Helm.Shell.Tabs;
using Helm.Shell.ViewModels;
using Helm.Shell.Views;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Gallery;

/// <summary>Which shell a Gallery entry shows.</summary>
internal enum ShellStage
{
    /// <summary>The menu pinned open beside the page, Home in front, five tabs.</summary>
    MenuOpen,
    /// <summary>The menu collapsed to its icon rail, Wallet in front.</summary>
    Rail,
    /// <summary>The rail open over the page for a moment (the mouse rests on it).</summary>
    Peek,
    /// <summary>"rem" typed in the search box: the results float under it.</summary>
    Search,
    /// <summary>A search that finds nothing.</summary>
    SearchNothing,
    /// <summary>The phone's drawer open over the dimmed page.</summary>
    Drawer,
    /// <summary>A tab torn off into a window of its own: only its tabs, no menu, no search.</summary>
    TabWindow,
    /// <summary>The title bar with the sync glyph in each of its states.</summary>
    SyncStates,
}

/// <summary>
/// The real shell (Helm.Shell.UI's ShellView) around the real pages of a made-up Helm: every PC tool with its own icon,
/// a few tabs open, the page in front a real Gallery page.
/// </summary>
internal static class ShellDemo
{
    public static IReadOnlyList<ShellToolItem> Tools() =>
    [
        Tool("habits", "Habits", ModuleGroup.Planning),
        Tool("missions", "Missions", ModuleGroup.Planning),
        Tool("notes", "Notes", ModuleGroup.Planning),
        Tool("quick-capture", "Quick Capture", ModuleGroup.Planning),
        Tool("tracker", "Tracker", ModuleGroup.Planning),
        Tool("novel-reader", "Novel Reader", ModuleGroup.MoneyAndMedia),
        Tool("scratch", "Scratch", ModuleGroup.MoneyAndMedia),
        Tool("wallet", "Wallet", ModuleGroup.MoneyAndMedia),
        Tool("ssh", "SSH", ModuleGroup.SecurityAndServers),
        Tool("vault", "Vault", ModuleGroup.SecurityAndServers),
        Tool("keyboard", "Helm Keyboard", ModuleGroup.WindowsAndDesktop, on: false),
        Tool("window-zones", "Window Zones", ModuleGroup.WindowsAndDesktop),
        Tool("vocab", "Vocabulary", ModuleGroup.Learning),
        Tool("slackbot", "Slack Report", ModuleGroup.Messages),
    ];

    private static ShellToolItem Tool(string id, string name, ModuleGroup group, bool on = true) =>
        new(id, name, group, Helm.ToolIcons.ToolIcons.Of(id), on);

    /// <summary>What the search finds: the pages, then a few settings cards inside them.</summary>
    public static SettingsSearch Search(IReadOnlyList<ShellToolItem> tools)
    {
        var entries = new List<SearchEntry>
        {
            new("Home", "Helm", ShellPages.Home),
            new("General", "Helm", ShellPages.General),
            new("Bug reports", "Helm", ShellPages.BugReports),
        };
        entries.AddRange(tools.Select(t => new SearchEntry(t.Name, ModuleGroups.DisplayName(t.Group), t.Id, Rank: 2)));
        entries.AddRange(
        [
            new("Reminders", "Tracker", "tracker", "reminders", 10),
            new("Remind me about bills", "Wallet", "wallet", "bill-reminders", 10),
            new("Remove old trash after", "Notes", "notes", "trash", 10),
            new("Remember the keyboard per web site", "Helm Keyboard", "keyboard", "per-site", 10),
            new("Sync", "General", ShellPages.General, "sync", 10),
            new("Theme", "General", ShellPages.General, "appearance", 10),
            new("Start with Windows", "General", ShellPages.General, "startup", 10),
        ]);
        return new SettingsSearch(entries.ToArray());
    }

    public static ShellViewModel Shell(SyncState sync = SyncState.Idle, FakeModule? module = null) => Build(new DemoSync(sync), module);

    private static ShellViewModel Build(DemoSync sync, FakeModule? module)
    {
        var tools = Tools();
        var host = new Host(module ?? new FakeModule("tool", "Tool"));
        // The green "just synced" lasts an hour here, so a screenshot catches it.
        var shell = new ShellViewModel(host, tools, Search(tools), new SyncIndicatorViewModel(sync, new AvaloniaUi(), TimeSpan.FromHours(1)));
        shell.Restore(new TabsState([ShellPages.Home, "notes", "wallet", "habits", "ssh"], ShellPages.Home));
        // The app starts with the icon rail; most demos show the menu open (the Rail and Peek stages show the rail).
        shell.Menu.IsPinned = true;
        return shell;
    }

    public static Control Create(FakeModule module, ShellStage stage)
    {
        if (stage == ShellStage.SyncStates) return SyncStates();
        var shell = Shell(module: module);
        switch (stage)
        {
            case ShellStage.Rail:
                shell.Menu.IsPinned = false;
                shell.Open("wallet");
                break;
            case ShellStage.Peek:
                shell.Menu.IsPinned = false;
                shell.Open("wallet");
                shell.Menu.IsPeeking = true;
                break;
            case ShellStage.Search:
                return Later(new ShellView(shell), () => shell.Search.Query = "rem");
            case ShellStage.SearchNothing:
                return Later(new ShellView(shell), () => shell.Search.Query = "printer");
            case ShellStage.Drawer:
                shell.Open("habits");
                break;
            case ShellStage.TabWindow:
                var torn = shell.Tabs.TearOff(shell.Tabs.Find("notes")!)!;
                shell.Tabs.Open("vault", torn);
                shell.Tabs.Open("notes", torn);
                return new ShellView(shell, shell.StripFor(torn));
        }
        var view = new ShellView(shell);
        return stage == ShellStage.Drawer ? Later(view, () => shell.Menu.IsDrawerOpen = true) : view;
    }

    /// <summary>Does <paramref name="act"/> once the view is on screen, as a click or a key would.</summary>
    private static Control Later(Control view, Action act)
    {
        view.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(act, Avalonia.Threading.DispatcherPriority.Background);
        return view;
    }

    /// <summary>The title bar four times: synced, syncing (turning), a problem (red), offline (faded).</summary>
    private static Control SyncStates()
    {
        var list = new StackPanel { Spacing = 18, Margin = new Thickness(0, 24, 0, 24) };
        foreach (var (state, label) in new[]
        {
            (SyncState.Idle, "Up to date"),
            (SyncState.Syncing, "Syncing (turns)"),
            (SyncState.Error, "Sync problem"),
            (SyncState.Offline, "Offline or not set up"),
        })
        {
            var bar = new ShellTitleBar { DataContext = Shell(state) };
            bar.Apply(narrow: false, mainWindow: true, windowButtons: true, brandWidth: 200, pageTitle: null, tabCount: 5);
            list.Children.Add(new TextBlock { Classes = { "eyebrow" }, Text = label.ToUpperInvariant(), Margin = new Thickness(24, 0, 0, -12) });
            list.Children.Add(bar);
        }
        var running = new DemoSync(SyncState.Syncing);
        var done = new ShellTitleBar { DataContext = Build(running, null) };
        done.Apply(narrow: false, mainWindow: true, windowButtons: true, brandWidth: 200, pageTitle: null, tabCount: 5);
        running.Finish();
        list.Children.Add(new TextBlock { Classes = { "eyebrow" }, Text = "JUST SYNCED (GREEN FOR A MOMENT)", Margin = new Thickness(24, 0, 0, -12) });
        list.Children.Add(done);
        return new Panel { Children = { new Backdrop(), new ScrollViewer { Content = list, VerticalAlignment = VerticalAlignment.Stretch } } };
    }

    /// <summary>The pages: real Gallery pages where one exists, else a plain glass page naming the tool.</summary>
    private sealed class Host(FakeModule module) : IShellHost
    {
        public Control CreatePage(string key) => key switch
        {
            ShellPages.Home => HomeDemo.Home(HomeStage.Busy),
            ShellPages.General => HomeDemo.General(desktop: true),
            ShellPages.BugReports => BugReportsDemo.Create(BugReportsStage.List),
            "notes" => NotesDemo.Create(module, NotesStage.Wall),
            "wallet" => WalletDemo.Create(module, WalletStage.Money),
            "habits" => HabitsDemo.Create(module, HabitsStage.Today),
            "vault" => VaultDemo.Create(module, VaultStage.Items),
            "ssh" => SshDemo.Create(module, SshStage.Connected),
            _ => Placeholder(key),
        };

        private static Control Placeholder(string key) => new Panel
        {
            Children =
            {
                new Backdrop(),
                new TextBlock { Classes = { "display" }, Text = key, Margin = new Thickness(40, 28) },
            },
        };

        public void Reveal(string key, Control page, string anchor) { }

        public void ReportBug() { }

        public void OpenWindow(TabStrip strip) { }

        public void CloseWindow(TabStrip strip) { }

        public void ShowWindow(TabStrip strip) { }
    }
}

/// <summary>A sync service that stays in the state it is given (the Gallery's sync glyph).</summary>
internal sealed class DemoSync(SyncState state) : ISyncService
{
    public SyncStatus Status { get; private set; } = new(state, state == SyncState.Idle ? DateTimeOffset.Now.AddMinutes(-2) : null,
        state == SyncState.Error ? "The server could not be reached" : null);

    public event EventHandler<SyncStatus>? StatusChanged;

    public Task<SyncRunResult> SyncNowAsync(CancellationToken ct = default) =>
        Task.FromResult(new SyncRunResult(SyncRunOutcome.Completed, 0, 0, 0));

    public void RequestSync() { }

    /// <summary>The sync that was running ends: the glyph goes green for a moment.</summary>
    public void Finish()
    {
        Status = new SyncStatus(SyncState.Idle, DateTimeOffset.Now, null);
        StatusChanged?.Invoke(this, Status);
    }
}
