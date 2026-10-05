namespace Helm.Modules.Tracker;

public enum ReportRange
{
    Last7Days,
    Last30Days,
    Last90Days,
    AllTime,
}

public sealed record ReportDay(DateOnly Date, int Count);

/// <summary>A repeating task and how many of its days were done in the report's range.</summary>
public sealed record ReportRepeat(string SeriesId, string Title, int Count);

/// <summary>A finished item as counted by a report (its last completion that was not reopened afterwards).</summary>
public sealed record ReportCompletion(TrackerEvent Event)
{
    /// <summary>Created → completed (waiting plus working).</summary>
    public TimeSpan LeadTime => (Event.CompletedAt ?? Event.At) - Event.CreatedAt;

    /// <summary>Started → completed; null when Start was never pressed.</summary>
    public TimeSpan? WorkTime => Event.StartedExplicitly && Event.StartedAt is { } s ? (Event.CompletedAt ?? Event.At) - s : null;

    /// <summary>Null when the item had no due date.</summary>
    public bool? OnTime(TimeZoneInfo zone) => TrackerDue.Moment(Event.DueAt, Event.DueDate, zone) is { } due
        ? (Event.CompletedAt ?? Event.At) <= due
        : null;
}

/// <summary>
/// Statistics over the history log for a time range: what was finished, how long it took, and per-day counts.
/// Pure: built from events, so it works the same on every device and after items are deleted.
/// </summary>
public sealed class TrackerReport
{
    private TrackerReport() { }

    public DateOnly? From { get; private init; }
    public DateOnly To { get; private init; }

    public IReadOnlyList<ReportCompletion> Completions { get; private init; } = [];

    /// <summary>Items created in the range (including ones deleted since).</summary>
    public int CreatedCount { get; private init; }

    public int CompletedCount => Completions.Count;

    public TimeSpan? AverageLeadTime { get; private init; }
    public TimeSpan? MedianLeadTime { get; private init; }

    /// <summary>Average over completions that had an explicit Start.</summary>
    public TimeSpan? AverageWorkTime { get; private init; }

    /// <summary>Completed on or before the due date, out of completions that had one; null without any.</summary>
    public double? OnTimeRate { get; private init; }

    /// <summary>Every day of the range (at most the last 90 days for "all time"), oldest first.</summary>
    public IReadOnlyList<ReportDay> Days { get; private init; } = [];

    public IReadOnlyDictionary<TrackerPriority, int> ByPriority { get; private init; } = new Dictionary<TrackerPriority, int>();

    /// <summary>Repeating tasks done in the range, the most done first.</summary>
    public IReadOnlyList<ReportRepeat> Repeats { get; private init; } = [];

    public static (DateOnly? From, DateOnly To) Bounds(ReportRange range, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        DateOnly? from = range switch
        {
            ReportRange.Last7Days => today.AddDays(-6),
            ReportRange.Last30Days => today.AddDays(-29),
            ReportRange.Last90Days => today.AddDays(-89),
            _ => null,
        };
        return (from, today);
    }

    /// <param name="events">The whole history, any order.</param>
    /// <param name="workspaceId">Only this workspace; null for all.</param>
    public static TrackerReport Build(IEnumerable<TrackerEvent> events, ReportRange range, DateTimeOffset now, TimeZoneInfo zone, string? workspaceId = null)
    {
        var (from, to) = Bounds(range, now, zone);
        var relevant = events
            // The old debt book's events (Helm 0.26 and older) are not tasks.
            .Where(e => e.WorkspaceKind == WorkspaceKind.Tasks && (workspaceId is null || e.WorkspaceId == workspaceId))
            .OrderBy(e => e.At)
            .ToList();

        DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        bool InRange(DateTimeOffset at) => (from is null || Day(at) >= from) && Day(at) <= to;

        // The completion that counts for an item is its last one, unless it was reopened afterwards.
        var pending = new Dictionary<string, TrackerEvent>(StringComparer.Ordinal);
        var created = 0;
        foreach (var e in relevant)
        {
            switch (e.Kind)
            {
                case TrackerEventKind.Created:
                    if (InRange(e.At)) created++;
                    break;
                case TrackerEventKind.Completed:
                    pending[e.ItemId] = e;
                    break;
                case TrackerEventKind.Reopened:
                    pending.Remove(e.ItemId);
                    break;
            }
        }
        var completions = pending.Values
            .Where(e => InRange(e.At))
            .OrderBy(e => e.At)
            .Select(e => new ReportCompletion(e))
            .ToList();

        var leads = completions.Select(c => c.LeadTime).OrderBy(t => t).ToList();
        var works = completions.Select(c => c.WorkTime).OfType<TimeSpan>().ToList();
        var dueResults = completions.Select(c => c.OnTime(zone)).OfType<bool>().ToList();

        var firstDay = from ?? (completions.Count > 0 ? Day(completions[0].Event.At) : to);
        if (to.DayNumber - firstDay.DayNumber > 89) firstDay = to.AddDays(-89);
        var perDay = completions.GroupBy(c => Day(c.Event.At)).ToDictionary(g => g.Key, g => g.Count());
        var days = new List<ReportDay>();
        for (var d = firstDay; d <= to; d = d.AddDays(1)) days.Add(new ReportDay(d, perDay.GetValueOrDefault(d)));

        return new TrackerReport
        {
            From = from,
            To = to,
            Completions = completions,
            CreatedCount = created,
            AverageLeadTime = leads.Count == 0 ? null : TimeSpan.FromTicks((long)leads.Average(t => t.Ticks)),
            MedianLeadTime = leads.Count == 0 ? null : Median(leads),
            AverageWorkTime = works.Count == 0 ? null : TimeSpan.FromTicks((long)works.Average(t => t.Ticks)),
            OnTimeRate = dueResults.Count == 0 ? null : dueResults.Count(ok => ok) / (double)dueResults.Count,
            Days = days,
            ByPriority = Enum.GetValues<TrackerPriority>().ToDictionary(p => p, p => completions.Count(c => c.Event.Priority == p)),
            Repeats = completions.Where(c => c.Event.SeriesId is not null)
                .GroupBy(c => c.Event.SeriesId!)
                .Select(g => new ReportRepeat(g.Key, g.OrderBy(c => c.Event.At).Last().Event.Title, g.Count()))
                .OrderByDescending(r => r.Count).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
        };
    }

    private static TimeSpan Median(IReadOnlyList<TimeSpan> sorted) => sorted.Count % 2 == 1
        ? sorted[sorted.Count / 2]
        : TimeSpan.FromTicks((sorted[sorted.Count / 2 - 1].Ticks + sorted[sorted.Count / 2].Ticks) / 2);
}
