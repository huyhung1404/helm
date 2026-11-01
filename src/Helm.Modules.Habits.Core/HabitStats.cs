namespace Helm.Modules.Habits;

/// <summary>Local days.</summary>
public static class HabitDays
{
    public static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>
    /// The last day that can be ticked: today for a habit to build; yesterday for one to quit, since a day is clean only
    /// once it is over. Everything about a habit (stats, grid, missed days) runs up to this day, as "today" does for a
    /// habit to build.
    /// </summary>
    public static DateOnly ActionDay(Habit habit, DateOnly today) => habit.Kind == HabitKind.Quit ? today.AddDays(-1) : today;

    /// <summary>
    /// Whether a day counts in the statistics: from the day the habit started to the day before its action day, plus
    /// the action day once it is done (an unfinished today never pulls a rate down).
    /// </summary>
    public static bool IsTracked(Habit habit, DateOnly day, DateOnly actionDay, IReadOnlySet<DateOnly> done) =>
        day >= habit.StartDay && (day < actionDay || (day == actionDay && done.Contains(day)));
}

/// <summary>
/// A habit's numbers as of today. <see cref="DoneToday"/> is about the habit's action day (for a habit to quit: whether
/// yesterday is ticked clean), and the streaks of a habit to quit count clean days. A day done in part counts in full
/// for the streaks (showing up is what builds a habit) and by its share in the rates (<see cref="Score30"/>).
/// </summary>
public sealed record HabitSummary(
    bool DoneToday,
    int CurrentStreak,
    int LongestStreak,
    int Done30,
    int Tracked30,
    int DoneYear,
    int TrackedYear,
    int MissedRun,
    double Score30,
    double ScoreYear)
{
    /// <summary>How much of the last 30 tracked days was done (a half day is half); null before any day counts.</summary>
    public double? Rate30 => Tracked30 == 0 ? null : Score30 / Tracked30;

    /// <summary>How much of this year's tracked days was done; null before any day counts.</summary>
    public double? RateYear => TrackedYear == 0 ? null : ScoreYear / TrackedYear;
}

/// <summary>Done and tracked days of one month, and how much of them was done (<see cref="Score"/>, a half day is 0.5).</summary>
public sealed record HabitMonth(int Month, int Done, int Tracked, double Score)
{
    public double? Rate => Tracked == 0 ? null : Score / Tracked;
}

/// <summary>Done and tracked days between two days, and how much of the tracked days was done.</summary>
public readonly record struct HabitCount(int Done, int Tracked, double Score);

/// <summary>
/// Streaks, rates and the run of missed days, worked out from the done days. Methods that take the habit take the real
/// today and work up to the habit's action day (<see cref="HabitDays.ActionDay"/>); the others take the day to count to.
/// The rates weigh each day by its share in <c>percents</c> (a day missing from it, or no percents at all, is a full one).
/// </summary>
public static class HabitStats
{
    public const int RecentDays = 30;

    /// <summary>
    /// The numbers as of today. With <paramref name="lapses"/>, an action day that has a reason (a habit to quit that
    /// slipped yesterday) breaks the current streak at once, instead of leaving it open until the day is done.
    /// </summary>
    public static HabitSummary Compute(Habit habit, IReadOnlySet<DateOnly> done, DateOnly today, IEnumerable<HabitLapse>? lapses = null,
        IReadOnlyDictionary<DateOnly, int>? percents = null)
    {
        var last = HabitDays.ActionDay(habit, today);
        var recent = Count(habit, done, today, last.AddDays(-(RecentDays - 1)), last, percents);
        var year = Count(habit, done, today, new DateOnly(last.Year, 1, 1), last, percents);
        var slipped = !done.Contains(last) && lapses is not null && lapses.Any(l => l.From <= last && l.To >= last);
        return new HabitSummary(done.Contains(last), slipped ? 0 : CurrentStreak(done, last), LongestStreak(done), recent.Done, recent.Tracked, year.Done,
            year.Tracked, MissedRun(habit, done, today), recent.Score, year.Score);
    }

    /// <summary>Done and tracked days between two days (inclusive), and how much of them was done.</summary>
    public static HabitCount Count(Habit habit, IReadOnlySet<DateOnly> done, DateOnly today, DateOnly from, DateOnly to,
        IReadOnlyDictionary<DateOnly, int>? percents = null)
    {
        var last = HabitDays.ActionDay(habit, today);
        int d = 0, t = 0;
        var score = 0.0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (!HabitDays.IsTracked(habit, day, last, done)) continue;
            t++;
            if (!done.Contains(day)) continue;
            d++;
            score += Share(percents, day);
        }
        return new HabitCount(d, t, score);
    }

    /// <summary>How much of a done day was done (0–1): its percent, or all of it when it has none.</summary>
    public static double Share(IReadOnlyDictionary<DateOnly, int>? percents, DateOnly day) =>
        percents is not null && percents.TryGetValue(day, out var p) ? HabitLimits.Percent(p) / 100.0 : 1;

    /// <summary>
    /// Days in a row up to <paramref name="today"/> (a habit's action day), or up to the day before while that day is not
    /// done yet (the chain is not broken yet).
    /// </summary>
    public static int CurrentStreak(IReadOnlySet<DateOnly> done, DateOnly today)
    {
        var day = done.Contains(today) ? today : today.AddDays(-1);
        var n = 0;
        while (done.Contains(day))
        {
            n++;
            day = day.AddDays(-1);
        }
        return n;
    }

    /// <summary>The longest run of done days, over all time or within one year.</summary>
    public static int LongestStreak(IReadOnlySet<DateOnly> done, int? year = null)
    {
        var days = done.Where(d => year is null || d.Year == year).OrderBy(d => d).ToList();
        int best = 0, run = 0;
        DateOnly? previous = null;
        foreach (var day in days)
        {
            run = previous is { } p && p.AddDays(1) == day ? run + 1 : 1;
            best = Math.Max(best, run);
            previous = day;
        }
        return best;
    }

    /// <summary>
    /// Tracked days in a row not done, ending the day before the action day (yesterday for a habit to build); 0 when
    /// that day was done or not tracked.
    /// </summary>
    public static int MissedRun(Habit habit, IReadOnlySet<DateOnly> done, DateOnly today)
    {
        var n = 0;
        for (var day = HabitDays.ActionDay(habit, today).AddDays(-1); day >= habit.StartDay && !done.Contains(day); day = day.AddDays(-1)) n++;
        return n;
    }

    /// <summary>The twelve months of a year.</summary>
    public static IReadOnlyList<HabitMonth> Months(Habit habit, IReadOnlySet<DateOnly> done, DateOnly today, int year,
        IReadOnlyDictionary<DateOnly, int>? percents = null) =>
        Enumerable.Range(1, 12).Select(m =>
        {
            var first = new DateOnly(year, m, 1);
            var c = Count(habit, done, today, first, first.AddMonths(1).AddDays(-1), percents);
            return new HabitMonth(m, c.Done, c.Tracked, c.Score);
        }).ToList();
}

/// <summary>
/// The colours of Habits, shared by both apps (ARGB). A card goes from red to green with how often the habit was done
/// lately; the grid uses GitHub's greens, one pair for each theme, deeper as more of the day was done.
/// </summary>
public static class HabitColors
{
    public const uint Red = 0xFFE5484D;
    public const uint Amber = 0xFFF5A524;
    public const uint Green = 0xFF30A46C;

    /// <summary>A habit with no tracked day yet.</summary>
    public const uint Neutral = 0xFF8B8D98;

    /// <summary>Done days: the green of a small share (least) blends into the green of all of it (most).</summary>
    public const uint DarkLeast = 0xFF196C2E;

    public const uint DarkMost = 0xFF56D364;

    public const uint LightLeast = 0xFF9BE9A8;

    public const uint LightMost = 0xFF216E39;

    /// <summary>A tracked day not done.</summary>
    public const uint DarkEmpty = 0x24FFFFFF;

    public const uint LightEmpty = 0x1A000000;

    /// <summary>A day a habit to quit slipped (a reason was written for it): a soft red, apart from a day not marked.</summary>
    public const uint DarkSlipped = 0xA6E5484D;

    public const uint LightSlipped = 0x8CE5484D;

    /// <summary>Days that do not count: before the habit started, and the rest of the year.</summary>
    public const uint DarkFaded = 0x0DFFFFFF;

    public const uint LightFaded = 0x0A000000;

    /// <summary>Red at 0 %, amber at 50 %, green at 100 %, blended in between; <see cref="Neutral"/> without a rate.</summary>
    public static uint ForRate(double? rate)
    {
        if (rate is not { } r || double.IsNaN(r)) return Neutral;
        r = Math.Clamp(r, 0, 1);
        return r <= 0.5 ? Blend(Red, Amber, r / 0.5) : Blend(Amber, Green, (r - 0.5) / 0.5);
    }

    /// <summary>The colour with another alpha (0-255).</summary>
    public static uint WithAlpha(uint argb, byte alpha) => (argb & 0x00FFFFFF) | ((uint)alpha << 24);

    /// <summary>"#AARRGGBB", which both XAML dialects read as a brush.</summary>
    public static string Hex(uint argb) => $"#{argb:X8}";

    /// <summary>The green of a done day, from <see cref="LightLeast"/> at 1 % to <see cref="LightMost"/> at 100 % (or the dark pair).</summary>
    public static uint ForPercent(int percent, bool dark)
    {
        var t = (Math.Clamp(percent, 1, 100) - 1) / 99.0;
        return dark ? Blend(DarkLeast, DarkMost, t) : Blend(LightLeast, LightMost, t);
    }

    private static uint Blend(uint a, uint b, double t)
    {
        static byte Mix(uint x, uint y, int shift, double t) => (byte)Math.Round(((x >> shift) & 0xFF) + ((((y >> shift) & 0xFF) - (double)((x >> shift) & 0xFF)) * t));
        return 0xFF000000 | ((uint)Mix(a, b, 16, t) << 16) | ((uint)Mix(a, b, 8, t) << 8) | Mix(a, b, 0, t);
    }
}
