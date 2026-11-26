using Avalonia.Controls;
using Helm.General;
using Helm.Home;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Gallery;

/// <summary>Which Home a Gallery entry shows.</summary>
internal enum HomeStage
{
    /// <summary>A busy day: something wrong with Helm, and things due in several tools.</summary>
    Busy,
    /// <summary>Nothing due, nothing wrong.</summary>
    AllClear,
}

/// <summary>The real Home and General views with a made-up Helm: today's tools, each with its own icon.</summary>
internal static class HomeDemo
{
    public static IReadOnlyList<HomeTool> AllTools() =>
    [
        Tool("habits", "Habits", "Planning"),
        Tool("missions", "Missions", "Planning"),
        Tool("notes", "Notes", "Planning"),
        Tool("quick-capture", "Quick Capture", "Planning"),
        Tool("tracker", "Tracker", "Planning"),
        Tool("slackbot", "Slack Report", "Planning", on: false),
        Tool("novel-reader", "Novel Reader", "Money & Media"),
        Tool("scratch", "Scratch", "Money & Media"),
        Tool("vocab", "Vocabulary", "Money & Media"),
        Tool("wallet", "Wallet", "Money & Media"),
        Tool("ssh", "SSH", "Security & Servers"),
        Tool("vault", "Vault", "Security & Servers"),
        Tool("keyboard", "Helm Keyboard", "Windows & Desktop", on: false),
        Tool("window-zones", "Window Zones", "Windows & Desktop"),
        Tool("workspace", "Workspace Controller", "Windows & Desktop"),
    ];

    private static HomeTool Tool(string id, string name, string group, bool on = true) =>
        new(id, name, group, Symbol.Apps, Helm.ToolIcons.ToolIcons.Of(id), on);

    public static Control Home(HomeStage stage)
    {
        var vm = new HomeViewModel(new Host(), "v0.88.0") { SyncLine = "synced 2 minutes ago", CaptureIcon = Helm.ToolIcons.ToolIcons.Of("quick-capture") };
        if (stage == HomeStage.AllClear) return new HomeView(vm);

        var tools = AllTools().ToDictionary(t => t.Id);
        static Task Done() => Task.CompletedTask;
        void Ticked(TodayItem item) { }
        vm.SetAlerts(
        [
            new HomeAlert("Ctrl+Alt+N is taken by another app", "Window Zones cannot use it. Pick another shortcut.", "Change", Done),
            new HomeAlert("Helm 0.88.1 is ready", "Restart Helm to install it (about 10 seconds).", "Restart", Done),
            new HomeAlert("The vault has not been backed up for 9 days", "Your backup folder could not be reached.", "Back up", Done),
        ]);
        vm.SetToday(
        [
            new TodayCard(tools["tracker"], "3 open today · 1 overdue",
            [
                new TodayItem("Trả tiền điện tháng 11", "Overdue · was due yesterday", isLate: true, tick: Ticked),
                new TodayItem("Gửi báo cáo tuần", "Today 17:00", tick: Ticked),
                new TodayItem("Ôn Aptis phần Writing", "Today 20:00 · 2 of 3 steps", tick: Ticked),
            ], "Open", Done, urgency: 90),
            new TodayCard(tools["habits"], "2 of 4 done",
            [
                new TodayItem("Đọc sách 20 phút", "12-day streak", tick: Ticked),
                new TodayItem("Tập thể dục", "3-day streak", tick: Ticked),
            ], "Open", Done, urgency: 60),
            new TodayCard(tools["wallet"], "Due soon",
            [
                new TodayItem("Internet FPT · 250.000 ₫", "Due tomorrow"),
                new TodayItem("Anh Minh owes you 2.000.000 ₫", "Said he'd pay back today"),
            ], "Open", Done, urgency: 70),
            new TodayCard(tools["missions"], "Aptis B1 · step 3 of 8",
            [
                new TodayItem("Write 150 words about your hometown", "Next step"),
            ], "Continue", Done, urgency: 40),
            new TodayCard(tools["vocab"], "18 words to review", [], "Review", Done, urgency: 30),
            new TodayCard(tools["workspace"], "Claude is waiting for you",
            [
                new TodayItem("helm · feat/home-ui", "Asked a question 4 minutes ago"),
            ], "Answer", Done, urgency: 80),
        ]);
        return new HomeView(vm);
    }

    public static Control General(bool desktop)
    {
        var vm = new GeneralViewModel(new Host(), "v0.88.0", desktop)
        {
            AccountName = "hung",
            SyncStatus = "Synced 2 minutes ago",
            SyncServer = "sync.huyhung1404.com",
            Usage = "38 MB of 1 GB used",
            UpdateTitle = "Update ready",
            UpdateSubtitle = "0.88.1 downloaded · restart to install",
            UpdateAction = "Restart to update",
            SettingsFolder = @"C:\Users\you\AppData\Roaming\Helm",
        };
        var tools = AllTools();
        vm.SetTools(desktop ? tools : tools.Where(t => t.Group != "Windows & Desktop"));
        foreach (var name in new[] { "Notes", "Tracker", "Habits", "Missions", "Wallet", "Vocabulary", "Novel Reader", "Scratch" })
            vm.SyncTargets.Add(new SyncTarget(name, true));
        vm.SyncTargets.Add(new SyncTarget("Vault", desktop));
        static Task NoOp(object _) => Task.CompletedTask;
        vm.Devices.Add(new SyncDevice("Work PC", "Seen just now · Windows 11", isThis: desktop, NoOp));
        vm.Devices.Add(new SyncDevice("Home PC", "Seen 3 days ago · Windows 11", isThis: false, NoOp));
        vm.Devices.Add(new SyncDevice("Pixel 8", "Seen 20 minutes ago · Android 15", isThis: !desktop, NoOp));
        vm.Jobs.Add(new BackgroundJob("Tracker", "Due-date reminders, every 10 minutes", "Since 08:12", NoOp));
        vm.Jobs.Add(new BackgroundJob("Workspace Controller", "Relays your VS Code sessions to the phone", "Since 08:12", NoOp));
        if (desktop) vm.Jobs.Add(new BackgroundJob("Window Zones", "Listens for Ctrl+Alt+N", "Since 08:12", NoOp));
        vm.Section("updates").NeedsAttention = true;
        return new GeneralView(vm);
    }

    private sealed class Host : IHomeHost, IGeneralHost
    {
        public Task<string> CaptureAsync(string text) => Task.FromResult(text.StartsWith("/n", StringComparison.Ordinal) ? "Added to Notes" : "Added to Tracker · Hôm nay");

        public void OpenWhatsNew() { }

        public Task SyncNowAsync() => Task.CompletedTask;

        public Task PairDeviceAsync(string code) => Task.CompletedTask;

        public Task ChangePassphraseAsync() => Task.CompletedTask;

        public Task TurnOffSyncAsync() => Task.CompletedTask;

        public Task UpdateAsync() => Task.CompletedTask;

        public void OpenAiFeatures() { }

        public void OpenSettingsFolder() { }

        public void OpenLogs() { }

        public Task ResetAllAsync() => Task.CompletedTask;
    }
}
