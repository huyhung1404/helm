namespace Helm.Modules.Habits;

/// <summary>A run of missed days that still needs its reason.</summary>
public sealed record HabitGap(string HabitId, DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>
/// Finds the runs of missed days the user must explain when they come back: at least <see cref="MinDays"/> tracked days
/// in a row not done, ending before today, within the last <see cref="LookBackDays"/> days. Days already covered by a
/// reason (written here or on another device) are left out, so a run that keeps growing asks again only once two more
/// days are missed.
/// </summary>
public static class HabitLapses
{
    public const int MinDays = 2;
    public const int LookBackDays = 30;

    public static IReadOnlyList<HabitGap> Pending(string habitId, Habit habit, IReadOnlySet<DateOnly> done, IEnumerable<HabitLapse> lapses, DateOnly today)
    {
        var covered = new HashSet<DateOnly>();
        foreach (var l in lapses)
        {
            for (var d = l.From; d <= l.To; d = d.AddDays(1)) covered.Add(d);
        }
        var start = today.AddDays(-LookBackDays);
        if (habit.StartDay > start) start = habit.StartDay;
        var gaps = new List<HabitGap>();
        DateOnly? runStart = null;
        for (var day = start; day <= today; day = day.AddDays(1))
        {
            var open = day < today && !done.Contains(day) && !covered.Contains(day);
            if (open)
            {
                runStart ??= day;
                continue;
            }
            if (runStart is { } from)
            {
                var to = day.AddDays(-1);
                if (to.DayNumber - from.DayNumber + 1 >= MinDays) gaps.Add(new HabitGap(habitId, from, to));
                runStart = null;
            }
        }
        return gaps;
    }

    /// <summary>
    /// Every run of missed days in a year (for the report and AI agents), oldest first, each with the reason that covers
    /// it, if any. A run is cut where a reason starts or ends, so a reason never seems to explain days it does not cover.
    /// </summary>
    public static IReadOnlyList<(HabitGap Gap, HabitLapse? Reason)> Missed(string habitId, Habit habit, IReadOnlySet<DateOnly> done, IReadOnlyList<HabitLapse> lapses, DateOnly today, int year)
    {
        var result = new List<(HabitGap, HabitLapse?)>();
        var first = new DateOnly(year, 1, 1);
        if (habit.StartDay > first) first = habit.StartDay;
        var last = new DateOnly(year, 12, 31);
        if (today.AddDays(-1) < last) last = today.AddDays(-1);
        DateOnly? runStart = null;
        HabitLapse? runReason = null;
        for (var day = first; day <= last.AddDays(1); day = day.AddDays(1))
        {
            var missed = day <= last && !done.Contains(day);
            // The newest reason that covers the day (an older one may overlap it).
            var reason = missed ? lapses.Where(l => l.From <= day && l.To >= day).MaxBy(l => l.WrittenAt) : null;
            if (runStart is { } from && (!missed || !ReferenceEquals(reason, runReason)))
            {
                result.Add((new HabitGap(habitId, from, day.AddDays(-1)), runReason));
                runStart = null;
            }
            if (missed && runStart is null)
            {
                runStart = day;
                runReason = reason;
            }
        }
        return result;
    }
}
