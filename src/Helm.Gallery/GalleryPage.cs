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
