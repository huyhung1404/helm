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
