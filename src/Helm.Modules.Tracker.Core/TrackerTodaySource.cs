using Helm.Core.Sync;
using Helm.Core.Today;

namespace Helm.Modules.Tracker;

/// <summary>Tracker's part of Home's "today": open tasks due today or already overdue, overdue first.</summary>
public sealed class TrackerTodaySource(TrackerStore store, TimeZoneInfo? zone = null) : ITodaySource
{
    /// <summary>Home shows a few; the headline counts all.</summary>
    public const int MaxEntries = 5;

    private readonly TimeZoneInfo _zone = zone ?? TimeZoneInfo.Local;

    public string ModuleId => TrackerIds.ModuleId;

    public event EventHandler? Changed
    {
        add => store.Changed += value;
        remove => store.Changed -= value;
    }

    public TodayList? Build()
    {
        var now = store.Now;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _zone).DateTime);
        var due = new List<(SyncedItem<TrackerItem> Task, DateOnly Day)>();
        foreach (var workspace in store.Workspaces())
        {
            foreach (var item in TrackerOrdering.Open(store.Items(workspace.Id)))
            {
                var day = DueDay(item.Value, _zone);
                if (day is { } d && d <= today) due.Add((item, d));
            }
        }
        if (due.Count == 0) return null;

        var ordered = due.OrderBy(x => x.Day).ThenByDescending(x => x.Task.Value.Priority).ToList();
        var late = ordered.Count(x => x.Day < today || x.Task.Value.DueAt is { } at && at < now);
        var entries = ordered.Take(MaxEntries).Select(x =>
        {
            var isLate = x.Day < today || x.Task.Value.DueAt is { } at && at < now;
            return new TodayEntry(x.Task.Id, x.Task.Value.Title, TrackerFormat.Due(x.Task.Value, now, _zone), isLate, CanTick: true);
        }).ToList();
        var headline = late > 0 ? $"{ordered.Count} open today · {late} overdue" : $"{ordered.Count} open today";
        return new TodayList(ModuleId, headline, entries, "Open", late > 0 ? 90 : 60);
    }

    public void Tick(string entryId, bool done)
    {
        if (done) store.Complete(entryId);
        else store.Reopen(entryId);
    }

    private static DateOnly? DueDay(TrackerItem item, TimeZoneInfo zone) =>
        item.DueAt is { } at ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime) : item.DueDate;
}
