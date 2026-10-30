namespace Helm.Modules.Habits;

/// <summary>Local days.</summary>
public static class HabitDays
{
    public static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>
    /// Whether a day counts in the statistics: from the day the habit started to yesterday, plus today once it is done
    /// (an unfinished today never pulls a rate down).
    /// </summary>
    public static bool IsTracked(Habit habit, DateOnly day, DateOnly today, IReadOnlySet<DateOnly> done) =>
        day >= habit.StartDay && (day < today || (day == today && done.Contains(day)));
}

/// <summary>A habit's numbers as of today.</summary>
public sealed record HabitSummary(
    bool DoneToday,
    int CurrentStreak,
    int LongestStreak,
    int Done30,
    int Tracked30,
    int DoneYear,
    int TrackedYear,
    int MissedRun)
{
    /// <summary>Share of the last 30 tracked days that were done; null before any day counts.</summary>
    public double? Rate30 => Tracked30 == 0 ? null : (double)Done30 / Tracked30;

    /// <summary>Share of this year's tracked days that were done; null before any day counts.</summary>
    public double? RateYear => TrackedYear == 0 ? null : (double)DoneYear / TrackedYear;
}

/// <summary>Done and tracked days of one month.</summary>
public sealed record HabitMonth(int Month, int Done, int Tracked)
{
    public double? Rate => Tracked == 0 ? null : (double)Done / Tracked;
}

/// <summary>Streaks, rates and the run of missed days, worked out from the done days.</summary>
public static class HabitStats
{
    public const int RecentDays = 30;

    public static HabitSummary Compute(Habit habit, IReadOnlySet<DateOnly> done, DateOnly today)
    {
        var doneToday = done.Contains(today);
        var (done30, tracked30) = Count(habit, done, today, today.AddDays(-(RecentDays - 1)), today);
        var (doneYear, trackedYear) = Count(habit, done, today, new DateOnly(today.Year, 1, 1), today);
        return new HabitSummary(doneToday, CurrentStreak(done, today), LongestStreak(done), done30, tracked30, doneYear, trackedYear, MissedRun(habit, done, today));
    }

    /// <summary>Done and tracked days between two days (inclusive).</summary>
    public static (int Done, int Tracked) Count(Habit habit, IReadOnlySet<DateOnly> done, DateOnly today, DateOnly from, DateOnly to)
    {
        int d = 0, t = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (!HabitDays.IsTracked(habit, day, today, done)) continue;
            t++;
            if (done.Contains(day)) d++;
        }
        return (d, t);
    }

    /// <summary>Days in a row up to today, or up to yesterday while today is not done yet (the chain is not broken yet).</summary>
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

    /// <summary>Tracked days in a row not done, ending yesterday (0 when yesterday was done or not tracked).</summary>
    public static int MissedRun(Habit habit, IReadOnlySet<DateOnly> done, DateOnly today)
    {
        var n = 0;
        for (var day = today.AddDays(-1); day >= habit.StartDay && !done.Contains(day); day = day.AddDays(-1)) n++;
        return n;
    }

    /// <summary>The done days in a row that end on <paramref name="day"/> (0 when it was not done).</summary>
    public static int RunEndingOn(IReadOnlySet<DateOnly> done, DateOnly day)
    {
        var n = 0;
        while (done.Contains(day))
        {
            n++;
            day = day.AddDays(-1);
        }
        return n;
    }

    /// <summary>The twelve months of a year.</summary>
    public static IReadOnlyList<HabitMonth> Months(Habit habit, IReadOnlySet<DateOnly> done, DateOnly today, int year) =>
        Enumerable.Range(1, 12).Select(m =>
        {
            var first = new DateOnly(year, m, 1);
            var (d, t) = Count(habit, done, today, first, first.AddMonths(1).AddDays(-1));
            return new HabitMonth(m, d, t);
        }).ToList();
}

/// <summary>
/// The colours of Habits, shared by both apps (ARGB). A card goes from red to green with how often the habit was done
/// lately; the grid uses GitHub's greens, one set for each theme.
/// </summary>
public static class HabitColors
{
    public const uint Red = 0xFFE5484D;
    public const uint Amber = 0xFFF5A524;
    public const uint Green = 0xFF30A46C;

    /// <summary>A habit with no tracked day yet.</summary>
    public const uint Neutral = 0xFF8B8D98;

    /// <summary>Done days, from a first day to a long run (index = level - 1).</summary>
    public static readonly uint[] DarkLevels = [0xFF196C2E, 0xFF2EA043, 0xFF3FB950, 0xFF56D364];

    public static readonly uint[] LightLevels = [0xFF9BE9A8, 0xFF40C463, 0xFF30A14E, 0xFF216E39];

    /// <summary>A tracked day not done.</summary>
    public const uint DarkEmpty = 0x24FFFFFF;

    public const uint LightEmpty = 0x1A000000;

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

    /// <summary>The green of a done day: deeper as the run that ends on it grows (1, 2-3, 4-6, 7+ days).</summary>
    public static int Level(int run) => run switch
    {
        <= 0 => 0,
        1 => 1,
        <= 3 => 2,
        <= 6 => 3,
        _ => 4,
    };

    private static uint Blend(uint a, uint b, double t)
    {
        static byte Mix(uint x, uint y, int shift, double t) => (byte)Math.Round(((x >> shift) & 0xFF) + ((((y >> shift) & 0xFF) - (double)((x >> shift) & 0xFF)) * t));
        return 0xFF000000 | ((uint)Mix(a, b, 16, t) << 16) | ((uint)Mix(a, b, 8, t) << 8) | Mix(a, b, 0, t);
    }
}
