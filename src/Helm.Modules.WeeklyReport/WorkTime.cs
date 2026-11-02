namespace Helm.Modules.WeeklyReport;

/// <summary>
/// Working time guessed from activity (commits, messages): moments less than <see cref="Gap"/> apart are one sitting,
/// which starts <see cref="Lead"/> before its first moment (the work before a commit); a day counts at most
/// <see cref="DayCap"/>. It is an estimate and the report says so.
/// </summary>
public static class WorkTime
{
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(90);

    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan DayCap = TimeSpan.FromHours(12);

    /// <summary>The estimated time per local day.</summary>
    public static IReadOnlyDictionary<DateOnly, TimeSpan> ByDay(IEnumerable<DateTimeOffset> moments)
    {
        var days = new SortedDictionary<DateOnly, TimeSpan>();
        foreach (var day in moments.Select(m => m.ToLocalTime()).GroupBy(m => DateOnly.FromDateTime(m.DateTime)))
        {
            var sorted = day.OrderBy(m => m).ToList();
            var total = TimeSpan.Zero;
            var start = sorted[0];
            var last = sorted[0];
            foreach (var m in sorted.Skip(1))
            {
                if (m - last > Gap)
                {
                    total += last - start + Lead;
                    start = m;
                }
                last = m;
            }
            total += last - start + Lead;
            days[day.Key] = total > DayCap ? DayCap : total;
        }
        return days;
    }

    /// <summary>The estimated time over all the days.</summary>
    public static TimeSpan Total(IEnumerable<DateTimeOffset> moments) =>
        ByDay(moments).Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);

    /// <summary>"9.5 h".</summary>
    public static string Hours(TimeSpan time) => $"{Math.Round(time.TotalHours * 2) / 2:0.#} h";
}
