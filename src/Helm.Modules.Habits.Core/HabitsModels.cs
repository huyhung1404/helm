namespace Helm.Modules.Habits;

public enum HabitKind
{
    /// <summary>Something to do every day, ticked the day it is done.</summary>
    Build,

    /// <summary>Something to stop doing: each day kept clean of it is ticked the day after, once it is over.</summary>
    Quit,
}

/// <summary>
/// A habit to build (something done once a day, ticked off day by day) or to quit (each clean day ticked the day
/// after). A record from an app before habits to quit has no kind, so it is one to build.
/// </summary>
public sealed record Habit
{
    public string Title { get; init; } = "";

    /// <summary>Set when the habit is created; it never changes, since a tick means something else for each kind.</summary>
    public HabitKind Kind { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The local day the habit was created: days before it are greyed out and never counted.</summary>
    public DateOnly StartDay { get; init; }

    /// <summary>Position in the lists (smaller first).</summary>
    public double Order { get; init; }
}

/// <summary>
/// One day the habit was done (for a habit to quit: a day kept clean of it). Its id is <c>habitId@yyyy-MM-dd</c> (<see cref="HabitsStore.CheckId"/>), so the same
/// day ticked on two devices is one record; unticking deletes it.
/// </summary>
public sealed record HabitCheck
{
    public string HabitId { get; init; } = "";

    public DateOnly Day { get; init; }

    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>
    /// How much of the habit was done that day, 1–100 (<see cref="HabitLimits.Percent"/>); 0 in a record from before
    /// shares were kept, which means all of it. A habit to quit is always 100.
    /// </summary>
    public int Percent { get; init; }
}

/// <summary>
/// Why a habit was skipped for a run of days, written by the user when they came back (for a habit to quit: what led to
/// a slip). Kept for the reports and for AI agents to look for patterns. Its id is <c>habitId@yyyy-MM-dd</c> of
/// <see cref="From"/>.
/// </summary>
public sealed record HabitLapse
{
    public string HabitId { get; init; } = "";

    /// <summary>The first day missed.</summary>
    public DateOnly From { get; init; }

    /// <summary>The last day missed (inclusive).</summary>
    public DateOnly To { get; init; }

    public string Reason { get; init; } = "";

    public DateTimeOffset WrittenAt { get; init; }

    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>Sizes the editors and AI agents keep to.</summary>
public static class HabitLimits
{
    public const int Title = 120;
    public const int Reason = 1000;
    public const int Habits = 50;

    /// <summary>The smallest share of a day the pop-up offers.</summary>
    public const int MinPercent = 5;

    /// <summary>A stored percent as 1–100: 0 (an older record) is all of it.</summary>
    public static int Percent(int percent) => percent <= 0 ? 100 : Math.Min(percent, 100);

    public static string Clip(string? text, int max)
    {
        var s = (text ?? "").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd();
    }
}
