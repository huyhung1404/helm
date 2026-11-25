using Avalonia.Controls;
using Helm.Core.Settings;
using Helm.Modules.Tracker;
using Helm.Modules.Tracker.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Gallery;

/// <summary>One Gallery entry: a name and how to build the real page over fake data. Adding a page = one line.</summary>
internal sealed record GalleryPage(string Name, Func<FakeModule, Control> Create)
{
    public static IReadOnlyList<GalleryPage> All { get; } =
    [
        new("Tracker · with tasks", m => Tracker(m, seed: true)),
        new("Tracker · empty", m => Tracker(m, seed: false)),
    ];

    private static Control Tracker(FakeModule module, bool seed)
    {
        var store = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(),
            new MemorySyncedLog<TrackerEvent>());
        if (seed)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var list = store.AddWorkspace("Hôm nay");
            store.AddItem(list, new TrackerItemDraft("Ôn Aptis phần Writing", TrackerPriority.High, today));
            store.AddItem(list, new TrackerItemDraft("Trả tiền điện tháng 11", DueDate: today.AddDays(-1)));
            var done = store.AddItem(list, new TrackerItemDraft("Gửi báo cáo tuần"));
            store.Complete(done);
            store.AddItem(list, new TrackerItemDraft("Thử Avalonia Gallery", Notes: "Spike bước 1"));
            store.AddWorkspace("Helm");
        }

        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var settings = new SettingsStoreFactory(new HelmPaths(dir));
        var vm = new TrackerViewModel(store, settings, new AvaloniaUi(), new YesDialogs(), new NoClipboard(),
            new TrackerReminderService(store, settings), NullLogger<TrackerViewModel>.Instance);
        return new TrackerView(module, vm, new NoHost());
    }

    private sealed class NoHost : ITrackerPageHost
    {
        public void ExportCalendar(string ics) { }

        public void Opened() { }
    }
}
