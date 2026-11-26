using Avalonia.Controls;
using Helm.Core.Settings;
using Helm.Gallery.Pages;
using Helm.Modules.Tracker;
using Helm.Modules.Tracker.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Gallery;

/// <summary>
/// One Gallery entry: where it sits in the list, its title and how to build the real page over fake data. A tool's
/// page is the real view from Helm.Modules.&lt;X&gt;.UI with its real view model over in-memory stores; adding one is
/// one line here (and a reference to that .UI project). <paramref name="Framed"/>: shown inside PageFrame (title,
/// inset, scrolling) like a plain page; a glass page draws its own full-bleed background and scrolls itself.
/// </summary>
internal sealed record GalleryPage(string Group, string Name, string Title, Func<FakeModule, Control> Create, bool Framed = true)
{
    public string Label => $"{Group} · {Name}";

    public static IReadOnlyList<GalleryPage> All { get; } =
    [
        new("Home", "Busy day", "Home", _ => HomeDemo.Home(HomeStage.Busy), Framed: false),
        new("Home", "All clear", "Home", _ => HomeDemo.Home(HomeStage.AllClear), Framed: false),
        new("General", "PC", "General", _ => HomeDemo.General(desktop: true), Framed: false),
        new("General", "Phone", "General", _ => HomeDemo.General(desktop: false), Framed: false),
        new("Tracker", "With tasks", "Tracker", m => Tracker(m, seed: true), Framed: false),
        new("Tracker", "First run", "Tracker", m => Tracker(m, seed: false), Framed: false),
        new("Vault", "Unlocked", "Vault", m => VaultDemo.Create(m, VaultStage.Items), Framed: false),
        new("Vault", "Locked", "Vault", m => VaultDemo.Create(m, VaultStage.Locked), Framed: false),
        new("Vault", "First run", "Vault", m => VaultDemo.Create(m, VaultStage.Setup), Framed: false),
        new("Vault", "Emergency Kit", "Vault", m => VaultDemo.Create(m, VaultStage.Kit), Framed: false),
        new("Vault", "Empty", "Vault", m => VaultDemo.Create(m, VaultStage.Empty), Framed: false),
        new("Notes", "Wall", "Notes", m => NotesDemo.Create(m, NotesStage.Wall), Framed: false),
        new("Notes", "Open note", "Notes", m => NotesDemo.Create(m, NotesStage.Open), Framed: false),
        new("Notes", "Empty", "Notes", m => NotesDemo.Create(m, NotesStage.Empty), Framed: false),
        new("Scratch", "Wall", "Scratch", m => ScratchDemo.Create(m, ScratchStage.Wall), Framed: false),
        new("Scratch", "Details (phone)", "Scratch", m => ScratchDemo.Create(m, ScratchStage.Wall, detail: true), Framed: false),
        new("Scratch", "Empty", "Scratch", m => ScratchDemo.Create(m, ScratchStage.Empty), Framed: false),
        new("Habits", "Today", "Habits", m => HabitsDemo.Create(m, HabitsStage.Today), Framed: false),
        new("Habits", "Report", "Habits", m => HabitsDemo.Create(m, HabitsStage.Report), Framed: false),
        new("Habits", "Mark a day", "Habits", m => HabitsDemo.Create(m, HabitsStage.MarkDay), Framed: false),
        new("Habits", "Celebration", "Habits", m => HabitsDemo.Create(m, HabitsStage.Celebration), Framed: false),
        new("Habits", "Reason for skipped days", "Habits", m => HabitsDemo.Create(m, HabitsStage.Reason), Framed: false),
        new("Habits", "First run", "Habits", m => HabitsDemo.Create(m, HabitsStage.FirstRun), Framed: false),
        new("Habits", "Settings", "Habits", m => HabitsDemo.Create(m, HabitsStage.Settings), Framed: false),
        new("Missions", "Mission", "Missions", m => MissionsDemo.Create(m, MissionsStage.Mission), Framed: false),
        new("Missions", "Insights", "Missions", m => MissionsDemo.Create(m, MissionsStage.Insights), Framed: false),
        new("Missions", "Roadmap step", "Missions", m => MissionsDemo.Create(m, MissionsStage.Roadmap), Framed: false),
        new("Missions", "Paused", "Missions", m => MissionsDemo.Create(m, MissionsStage.Paused), Framed: false),
        new("Missions", "New mission", "Missions", m => MissionsDemo.Create(m, MissionsStage.NewMission), Framed: false),
        new("Missions", "Paste the answer", "Missions", m => MissionsDemo.Create(m, MissionsStage.Import), Framed: false),
        new("Missions", "New plan", "Missions", m => MissionsDemo.Create(m, MissionsStage.Replan), Framed: false),
        new("Missions", "Phase done", "Missions", m => MissionsDemo.Create(m, MissionsStage.PhaseDone), Framed: false),
        new("Missions", "Mission done", "Missions", m => MissionsDemo.Create(m, MissionsStage.MissionDone), Framed: false),
        new("Missions", "First run", "Missions", m => MissionsDemo.Create(m, MissionsStage.FirstRun), Framed: false),
        new("Missions", "Settings", "Missions", m => MissionsDemo.Create(m, MissionsStage.Settings), Framed: false),
        new("Bug reports", "List", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.List), Framed: false),
        new("Bug reports", "Agent asks", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.AgentAsks), Framed: false),
        new("Bug reports", "Fix to check", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.FixToCheck), Framed: false),
        new("Bug reports", "Crash", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.Crash), Framed: false),
        new("Bug reports", "Closed", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.Closed), Framed: false),
        new("Bug reports", "Black box", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.BlackBox), Framed: false),
        new("Bug reports", "Close sheet", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.CloseSheet), Framed: false),
        new("Bug reports", "Reopen sheet", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.ReopenSheet), Framed: false),
        new("Bug reports", "Picture", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.Picture), Framed: false),
        new("Bug reports", "Settings (phone)", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.Settings), Framed: false),
        new("Bug reports", "Empty", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.Empty), Framed: false),
        new("Bug reports", "Report form", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.Form), Framed: false),
        new("Bug reports", "Report form (phone)", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.FormPhone), Framed: false),
        new("Bug reports", "Report a crash", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.FormCrash), Framed: false),
        new("Bug reports", "Report sent", "Bug reports", _ => BugReportsDemo.Create(BugReportsStage.FormSent), Framed: false),
        new("Slack Report", "To do", "Slack Report", m => SlackBotDemo.Create(m, SlackBotStage.ToDo), Framed: false),
        new("Slack Report", "Worth knowing", "Slack Report", m => SlackBotDemo.Create(m, SlackBotStage.Knowing), Framed: false),
        new("Slack Report", "Handled today", "Slack Report", m => SlackBotDemo.Create(m, SlackBotStage.Handled), Framed: false),
        new("Slack Report", "Reply", "Slack Report", m => SlackBotDemo.Create(m, SlackBotStage.Reply), Framed: false),
        new("Slack Report", "Claude Code missing", "Slack Report", m => SlackBotDemo.Create(m, SlackBotStage.SortProblem), Framed: false),
        new("Slack Report", "All clear", "Slack Report", m => SlackBotDemo.Create(m, SlackBotStage.AllClear), Framed: false),
        new("Slack Report", "First run (not signed in)", "Slack Report", m => SlackBotDemo.Create(m, SlackBotStage.FirstRun), Framed: false),
        new("Slack Report", "Settings", "Slack Report", m => SlackBotDemo.Create(m, SlackBotStage.Settings), Framed: false),
        new("Slack Report", "Settings (lower part)", "Slack Report", m => SlackBotDemo.Create(m, SlackBotStage.SettingsEnd), Framed: false),
        new("Slack Report", "Settings (connect)", "Slack Report", m => SlackBotDemo.Create(m, SlackBotStage.SettingsConnect), Framed: false),
        new("Window Zones", "Zones", "Window Zones", m => WindowZonesDemo.Create(m, WindowZonesStage.Zones), Framed: false),
        new("Window Zones", "Edit zones", "Window Zones", m => WindowZonesDemo.Create(m, WindowZonesStage.Editor), Framed: false),
        new("Window Zones", "Shortcut taken", "Window Zones", m => WindowZonesDemo.Create(m, WindowZonesStage.ShortcutTaken), Framed: false),
        new("Window Zones", "First run", "Window Zones", m => WindowZonesDemo.Create(m, WindowZonesStage.FirstRun), Framed: false),
        new("Window Zones", "Settings", "Window Zones", m => WindowZonesDemo.Create(m, WindowZonesStage.Settings), Framed: false),
        new("Window Zones", "Settings (lower part)", "Window Zones", m => WindowZonesDemo.Create(m, WindowZonesStage.SettingsEnd), Framed: false),
        new("Zone editor", "Toolbar", "Window Zones", m => WindowZonesDemo.Screen(m, ZoneScreenStage.Toolbar), Framed: false),
        new("Zone editor", "Other monitor", "Window Zones", m => WindowZonesDemo.Screen(m, ZoneScreenStage.OtherMonitor), Framed: false),
        new("Zone tabs", "Three windows", "Window Zones", m => WindowZonesDemo.Screen(m, ZoneScreenStage.Tabs), Framed: false),
        new("Zone tabs", "Many windows", "Window Zones", m => WindowZonesDemo.Screen(m, ZoneScreenStage.ManyTabs), Framed: false),
        new("Novel Reader", "Library", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Library), Framed: false),
        new("Novel Reader", "Library (phone)", "Novel Reader", m => NovelDemo.Create(m, NovelStage.LibraryPhone), Framed: false),
        new("Novel Reader", "First run", "Novel Reader", m => NovelDemo.Create(m, NovelStage.FirstRun), Framed: false),
        new("Novel Reader", "No dictionaries", "Novel Reader", m => NovelDemo.Create(m, NovelStage.NoDictionaries), Framed: false),
        new("Novel Reader", "Adding a novel", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Adding), Framed: false),
        new("Novel Reader", "Reading", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Reading), Framed: false),
        new("Novel Reader", "Reading aloud", "Novel Reader", m => NovelDemo.Create(m, NovelStage.ReadingAloud), Framed: false),
        new("Novel Reader", "Study mode", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Study), Framed: false),
        new("Novel Reader", "A word", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Word), Framed: false),
        new("Novel Reader", "A held sentence", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Sentence), Framed: false),
        new("Novel Reader", "Names", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Names), Framed: false),
        new("Novel Reader", "Wrong translations", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Reports), Framed: false),
        new("Novel Reader", "Report a wrong translation", "Novel Reader", m => NovelDemo.Create(m, NovelStage.ReportForm), Framed: false),
        new("Novel Reader", "Chapters", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Chapters), Framed: false),
        new("Novel Reader", "Reading options", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Options), Framed: false),
        new("Novel Reader", "A novel", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Book), Framed: false),
        new("Novel Reader", "Settings", "Novel Reader", m => NovelDemo.Create(m, NovelStage.Settings), Framed: false),
        new("Novel Reader", "Settings (phone)", "Novel Reader", m => NovelDemo.Create(m, NovelStage.SettingsPhone), Framed: false),
        new("SSH", "Connected", "SSH", m => SshDemo.Create(m, SshStage.Connected), Framed: false),
        new("SSH", "Connected (phone)", "SSH", m => SshDemo.Create(m, SshStage.Phone), Framed: false),
        new("SSH", "Password", "SSH", m => SshDemo.Create(m, SshStage.Password), Framed: false),
        new("SSH", "AI agent tab", "SSH", m => SshDemo.Create(m, SshStage.Agent), Framed: false),
        new("SSH", "Server menu", "SSH", m => SshDemo.Create(m, SshStage.Menu), Framed: false),
        new("SSH", "Menu item form", "SSH", m => SshDemo.Create(m, SshStage.MenuForm), Framed: false),
        new("SSH", "Server key changed", "SSH", m => SshDemo.Create(m, SshStage.KeyChanged), Framed: false),
        new("SSH", "First run", "SSH", m => SshDemo.Create(m, SshStage.FirstRun), Framed: false),
        new("SSH", "Settings", "SSH", m => SshDemo.Create(m, SshStage.Settings), Framed: false),
        new("SSH", "Edit a server", "SSH", m => SshDemo.Create(m, SshStage.EditServer), Framed: false),
        new("Vocabulary", "Learn", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Learn), Framed: false),
        new("Vocabulary", "Answer", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Answer), Framed: false),
        new("Vocabulary", "Type the word", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Typed), Framed: false),
        new("Vocabulary", "Fill the gap", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Gap), Framed: false),
        new("Vocabulary", "Pinyin card", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Pinyin), Framed: false),
        new("Vocabulary", "Card menu", "Vocabulary", m => VocabDemo.Create(m, VocabStage.CardMenu), Framed: false),
        new("Vocabulary", "Words", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Words), Framed: false),
        new("Vocabulary", "Analyzing", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Analyzing), Framed: false),
        new("Vocabulary", "Word page", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Word), Framed: false),
        new("Vocabulary", "Chinese word", "Vocabulary", m => VocabDemo.Create(m, VocabStage.ChineseWord), Framed: false),
        new("Vocabulary", "Roots", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Roots), Framed: false),
        new("Vocabulary", "Root practice", "Vocabulary", m => VocabDemo.Create(m, VocabStage.RootPractice), Framed: false),
        new("Vocabulary", "Family", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Family), Framed: false),
        new("Vocabulary", "Analysis", "Vocabulary", m => VocabDemo.Create(m, VocabStage.AnalysisSheet), Framed: false),
        new("Vocabulary", "Add text", "Vocabulary", m => VocabDemo.Create(m, VocabStage.AddText), Framed: false),
        new("Vocabulary", "Text words", "Vocabulary", m => VocabDemo.Create(m, VocabStage.TextWords), Framed: false),
        new("Vocabulary", "Edit", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Edit), Framed: false),
        new("Vocabulary", "Settings", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Settings), Framed: false),
        new("Vocabulary", "Waiting", "Vocabulary", m => VocabDemo.Create(m, VocabStage.Waiting), Framed: false),
        new("Vocabulary", "First run", "Vocabulary", m => VocabDemo.Create(m, VocabStage.FirstRun), Framed: false),
        new("Wallet", "Money", "Wallet", m => WalletDemo.Create(m, WalletStage.Money), Framed: false),
        new("Wallet", "Transactions", "Wallet", m => WalletDemo.Create(m, WalletStage.Transactions), Framed: false),
        new("Wallet", "To categorize", "Wallet", m => WalletDemo.Create(m, WalletStage.ToCategorize), Framed: false),
        new("Wallet", "Add a transaction", "Wallet", m => WalletDemo.Create(m, WalletStage.Add), Framed: false),
        new("Wallet", "Transaction details", "Wallet", m => WalletDemo.Create(m, WalletStage.Details), Framed: false),
        new("Wallet", "Lent, borrowed…", "Wallet", m => WalletDemo.Create(m, WalletStage.DebtForm), Framed: false),
        new("Wallet", "Fund", "Wallet", m => WalletDemo.Create(m, WalletStage.Fund), Framed: false),
        new("Wallet", "New fund", "Wallet", m => WalletDemo.Create(m, WalletStage.FundForm), Framed: false),
        new("Wallet", "Debts", "Wallet", m => WalletDemo.Create(m, WalletStage.Debts), Framed: false),
        new("Wallet", "Person", "Wallet", m => WalletDemo.Create(m, WalletStage.Person), Framed: false),
        new("Wallet", "Add a debt", "Wallet", m => WalletDemo.Create(m, WalletStage.AddDebt), Framed: false),
        new("Wallet", "Goals", "Wallet", m => WalletDemo.Create(m, WalletStage.Goals), Framed: false),
        new("Wallet", "Goal", "Wallet", m => WalletDemo.Create(m, WalletStage.Goal), Framed: false),
        new("Wallet", "Insights", "Wallet", m => WalletDemo.Create(m, WalletStage.Insights), Framed: false),
        new("Wallet", "Settings", "Wallet", m => WalletDemo.Create(m, WalletStage.Settings), Framed: false),
        new("Wallet", "Phone setup", "Wallet", m => WalletDemo.Create(m, WalletStage.PhoneSetup), Framed: false),
        new("Wallet", "Phone settings", "Wallet", m => WalletDemo.Create(m, WalletStage.PhoneSettings), Framed: false),
        new("Wallet", "First run", "Wallet", m => WalletDemo.Create(m, WalletStage.FirstRun), Framed: false),
        new("Wallet", "Goals first run", "Wallet", m => WalletDemo.Create(m, WalletStage.GoalsFirstRun), Framed: false),
        new("Base", "Controls", "Controls", _ => new ControlsPage()),
    ];

    private static Control Tracker(FakeModule module, bool seed)
    {
        var store = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(),
            new MemorySyncedLog<TrackerEvent>());
        if (seed)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var now = DateTimeOffset.Now;
            var list = store.AddWorkspace("Hôm nay");
            store.AddItem(list, new TrackerItemDraft("Trả tiền điện tháng 11", TrackerPriority.Urgent, today.AddDays(-1)));
            var aptis = store.AddItem(list, new TrackerItemDraft("Ôn Aptis phần Writing", TrackerPriority.High, DueAt: now.Date.AddHours(20)));
            store.Start(aptis);
            store.Complete(store.AddItem(list, new TrackerItemDraft("Đọc đề bài", ParentId: aptis)));
            store.Complete(store.AddItem(list, new TrackerItemDraft("Dàn ý 3 ý chính", ParentId: aptis)));
            store.AddItem(list, new TrackerItemDraft("Viết 150 từ", ParentId: aptis));
            store.AddItem(list, new TrackerItemDraft("Gửi báo cáo tuần", DueAt: now.Date.AddHours(17)));
            store.AddItem(list, new TrackerItemDraft("Gọi điện cho mẹ", DueDate: today.AddDays(1)));
            store.AddItem(list, new TrackerItemDraft("Đặt lịch nha sĩ", Notes: "Hỏi lịch sáng thứ Bảy"));
            store.AddItem(list, new TrackerItemDraft("Tưới cây", RepeatDaily: true, DueDate: today));
            store.Complete(store.AddItem(list, new TrackerItemDraft("Mua cà phê")));
            store.Complete(store.AddItem(list, new TrackerItemDraft("Sửa lỗi Backspace của bộ gõ")));

            var helm = store.AddWorkspace("Helm");
            store.AddItem(helm, new TrackerItemDraft("Chuyển Tracker sang Avalonia", TrackerPriority.High));
            store.AddItem(helm, new TrackerItemDraft("Thử DPI trên hai màn hình"));
            store.AddItem(helm, new TrackerItemDraft("Phát hành v0.87.0", DueDate: today.AddDays(-2)));

            var home = store.AddWorkspace("Nhà");
            store.AddItem(home, new TrackerItemDraft("Thay bóng đèn bếp"));
            store.AddWorkspace("Mua sắm");
        }

        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var settings = new SettingsStoreFactory(new HelmPaths(dir));
        var vm = new TrackerViewModel(store, settings, new AvaloniaUi(), new YesDialogs(), new NoClipboard(),
            new TrackerReminderService(store, settings), NullLogger<TrackerViewModel>.Instance);
        return new TrackerView(module, vm, new NoTrackerHost());
    }

    private sealed class NoTrackerHost : ITrackerPageHost
    {
        public void ExportCalendar(string ics) { }

        public void Opened() { }
    }
}
