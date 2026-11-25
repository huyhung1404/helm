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
        new("Tracker", "With tasks", "Tracker", m => Tracker(m, seed: true), Framed: false),
        new("Tracker", "First run", "Tracker", m => Tracker(m, seed: false), Framed: false),
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

        // The settings page is still WPF (Helm's TrackerPage); the Gallery has nothing to open yet.
        public void OpenSettings() { }
    }
}
