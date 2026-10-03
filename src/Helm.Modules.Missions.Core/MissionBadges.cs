using Helm.Core.Sync;

namespace Helm.Modules.Missions;

/// <summary>A badge: earned (with when) or still to earn.</summary>
public sealed record MissionBadge(string Id, string Title, string Description, DateTimeOffset? EarnedAt)
{
    public bool Earned => EarnedAt is not null;
}

/// <summary>
/// Badges across all missions, worked out from the history and the missions themselves each time (nothing extra is
/// stored or synced, so every device and older Helm versions agree). The list and order are fixed; a badge is earned
/// at the moment its condition was first met.
/// </summary>
public static class MissionBadges
{
    private sealed record Def(string Id, string Title, string Description);

    private static readonly Def[] Defs =
    [
        new("first-step", "First step", "Complete a step."),
        new("steps-10", "Ten steps", "Complete 10 steps."),
        new("steps-50", "Fifty steps", "Complete 50 steps."),
        new("steps-100", "A hundred steps", "Complete 100 steps."),
        new("streak-3", "On a roll", "Make progress 3 days in a row."),
        new("streak-7", "A full week", "Make progress 7 days in a row."),
        new("streak-30", "Unstoppable", "Make progress 30 days in a row."),
        new("phase-1", "Phase cleared", "Finish a phase."),
        new("mission-1", "Mission complete", "Finish a mission."),
        new("mission-3", "Three for three", "Finish 3 missions."),
        new("ahead", "Ahead of plan", "Finish a mission in less time than planned."),
        new("deadline", "Beat the clock", "Finish a mission on or before its deadline."),
        new("comeback", "Comeback", "Complete a step after pausing or abandoning a mission."),
    ];

    public static int Count => Defs.Length;

    public static IReadOnlyList<MissionBadge> Compute(MissionsStore store, TimeZoneInfo zone) =>
        Compute(store.Missions(), id => store.Steps(id).Select(s => s.Value).ToList(), store.History(), zone);

    public static IReadOnlyList<MissionBadge> Compute(
        IReadOnlyList<SyncedItem<Mission>> missions,
        Func<string, IReadOnlyList<MissionStep>> steps,
        IReadOnlyList<MissionEvent> history,
        TimeZoneInfo zone)
    {
        var earned = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        void Earn(string id, DateTimeOffset at)
        {
            if (!earned.TryGetValue(id, out var was) || at < was) earned[id] = at;
        }

        // Steps done (a reopened step no longer counts until it is done again), phases and missions, in order.
        var done = new HashSet<string>(StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        var resumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in history.OrderBy(e => e.At))
        {
            switch (e.Kind)
            {
                case MissionEventKind.StepCompleted when e.StepId is { } step:
                    if (done.Add(step))
                    {
                        if (done.Count >= 1) Earn("first-step", e.At);
                        if (done.Count >= 10) Earn("steps-10", e.At);
                        if (done.Count >= 50) Earn("steps-50", e.At);
                        if (done.Count >= 100) Earn("steps-100", e.At);
                    }
                    if (resumed.Contains(e.MissionId)) Earn("comeback", e.At);
                    break;
                case MissionEventKind.StepReopened when e.StepId is { } step:
                    done.Remove(step);
                    break;
                case MissionEventKind.PhaseCompleted:
                    Earn("phase-1", e.At);
                    break;
                case MissionEventKind.MissionCompleted:
                    if (finished.Add(e.MissionId))
                    {
                        if (finished.Count >= 1) Earn("mission-1", e.At);
                        if (finished.Count >= 3) Earn("mission-3", e.At);
                    }
                    break;
                case MissionEventKind.Resumed:
                    resumed.Add(e.MissionId);
                    break;
            }
        }

        // Days in a row with progress, over every mission.
        var days = history.Where(e => e.Kind is MissionEventKind.StepCompleted or MissionEventKind.ChecklistTicked)
            .GroupBy(e => MissionPace.Day(e.At, zone))
            .Select(g => (Day: g.Key, At: g.Min(e => e.At)))
            .OrderBy(d => d.Day)
            .ToList();
        var run = 0;
        for (var i = 0; i < days.Count; i++)
        {
            run = i > 0 && days[i].Day.DayNumber - days[i - 1].Day.DayNumber == 1 ? run + 1 : 1;
            if (run >= 3) Earn("streak-3", days[i].At);
            if (run >= 7) Earn("streak-7", days[i].At);
            if (run >= 30) Earn("streak-30", days[i].At);
        }

        // Finished missions against their plan and deadline.
        foreach (var m in missions.Where(m => m.Value is { Status: MissionStatus.Completed, CompletedAt: not null, StartedAt: not null }))
        {
            var at = m.Value.CompletedAt!.Value;
            var pace = MissionPace.Compute(m.Value, steps(m.Id), [], at, zone);
            if (pace.Total > 0 && pace.ElapsedDays < pace.PlannedDays) Earn("ahead", at);
            if (m.Value.Deadline is { } deadline && MissionPace.Day(at, zone) <= deadline) Earn("deadline", at);
        }

        return Defs.Select(d => new MissionBadge(d.Id, d.Title, d.Description, earned.TryGetValue(d.Id, out var at) ? at : null)).ToList();
    }
}
