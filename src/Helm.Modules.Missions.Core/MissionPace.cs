namespace Helm.Modules.Missions;

/// <summary>Where a mission stands against its plan (all computed from the records; nothing extra is stored).</summary>
public sealed record MissionPaceInfo
{
    public int Done { get; init; }
    public int Skipped { get; init; }
    public int Total { get; init; }

    /// <summary>0-based index of the current step's phase (the last phase when everything is done).</summary>
    public int PhaseIndex { get; init; }

    public int PhaseCount { get; init; }

    /// <summary>Sum of every step's estimate.</summary>
    public double PlannedDays { get; init; }

    /// <summary>Days the mission has been running (paused time left out); 0 before it starts.</summary>
    public double ElapsedDays { get; init; }

    /// <summary>Whole days behind the plan (positive) or ahead of it (negative); 0 on track or not started.</summary>
    public int DaysOff { get; init; }

    /// <summary>When it should end at the pace so far (local day); null before it starts or once it is over.</summary>
    public DateOnly? ProjectedFinish { get; init; }

    /// <summary>When the plan says it ends (start + planned days + paused time).</summary>
    public DateOnly? PlannedFinish { get; init; }

    /// <summary>Days of progress in a row, up to today (or yesterday, while today has none yet).</summary>
    public int Streak { get; init; }

    public int LongestStreak { get; init; }

    public double Percent => Total == 0 ? 0 : Done * 100.0 / Total;
}

/// <summary>
/// The pace of a mission. The planned date of step <i>i</i> is the start plus the estimates of steps 1…i (paused time
/// added); being past the current step's planned date is "behind", having finished steps earlier than planned is
/// "ahead". The projected finish stretches the remaining estimate by the pace so far (actual ÷ planned days of the
/// done steps, kept between 0.5 and 3 so one odd step does not swing it).
/// </summary>
public static class MissionPace
{
    public static MissionPaceInfo Compute(Mission mission, IReadOnlyList<MissionStep> steps, IReadOnlyList<MissionEvent> history, DateTimeOffset now, TimeZoneInfo zone)
    {
        var total = steps.Count;
        var done = steps.Count(s => s.IsDone);
        var skipped = steps.Count(s => s.Skipped && s.IsDone);
        var current = steps.FirstOrDefault(s => !s.IsDone);
        var phaseIndex = current is not null ? mission.PhaseIndex(current.PhaseKey) : mission.Phases.Count - 1;
        var planned = steps.Sum(s => s.EstimateDays);
        var (streak, longest) = Streaks(history, now, zone);

        var info = new MissionPaceInfo
        {
            Done = done,
            Skipped = skipped,
            Total = total,
            PhaseIndex = Math.Max(0, phaseIndex),
            PhaseCount = mission.Phases.Count,
            PlannedDays = planned,
            Streak = streak,
            LongestStreak = longest,
        };
        if (mission.StartedAt is not { } start) return info;

        var end = mission.CompletedAt ?? now;
        var paused = mission.PausedTotal + (mission.PausedAt is { } since && end > since ? end - since : TimeSpan.Zero);
        var elapsed = Math.Max(0, (end - start - paused).TotalDays);
        var plannedFinish = Day(start + paused + TimeSpan.FromDays(planned), zone);
        info = info with { ElapsedDays = elapsed, PlannedFinish = plannedFinish };
        if (mission.Status is MissionStatus.Completed or MissionStatus.Abandoned || current is null) return info;

        var doneDays = steps.Where(s => s.IsDone).Sum(s => s.EstimateDays);
        var throughCurrent = doneDays + current.EstimateDays;
        var daysOff = elapsed > throughCurrent ? (int)Math.Floor(elapsed - throughCurrent)
            : elapsed < doneDays ? -(int)Math.Floor(doneDays - elapsed)
            : 0;
        // Pace from the done steps; with none done yet, the plan as it is.
        var ratio = doneDays > 0 ? Math.Clamp(elapsed / doneDays, 0.5, 3) : 1;
        // The current step has been running for a while already; what is left of it is at least a quarter of it.
        var currentSince = current.StartedAt ?? steps.LastOrDefault(s => s.IsDone)?.CompletedAt ?? start;
        var onCurrent = Math.Max(0, (now - currentSince).TotalDays);
        var leftOfCurrent = Math.Max(current.EstimateDays * ratio - onCurrent, current.EstimateDays * ratio * 0.25);
        var left = leftOfCurrent + (planned - throughCurrent) * ratio;
        return info with { DaysOff = daysOff, ProjectedFinish = Day(now + TimeSpan.FromDays(left), zone) };
    }

    /// <summary>Current and longest run of local days with a completed step or a ticked checklist item.</summary>
    public static (int Current, int Longest) Streaks(IReadOnlyList<MissionEvent> history, DateTimeOffset now, TimeZoneInfo zone)
    {
        var days = history.Where(e => e.Kind is MissionEventKind.StepCompleted or MissionEventKind.ChecklistTicked)
            .Select(e => Day(e.At, zone)).Distinct().OrderBy(d => d).ToList();
        if (days.Count == 0) return (0, 0);
        var longest = 1;
        var run = 1;
        for (var i = 1; i < days.Count; i++)
        {
            run = days[i].DayNumber - days[i - 1].DayNumber == 1 ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }
        var today = Day(now, zone);
        var last = days[^1];
        var current = today.DayNumber - last.DayNumber <= 1 ? run : 0;
        return (current, longest);
    }

    public static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
}
