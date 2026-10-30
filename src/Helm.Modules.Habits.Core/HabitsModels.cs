namespace Helm.Modules.Habits;

/// <summary>A habit to build: something done once a day, ticked off day by day.</summary>
public sealed record Habit
{
    public string Title { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The local day the habit was created: days before it are greyed out and never counted.</summary>
    public DateOnly StartDay { get; init; }

    /// <summary>Position in the lists (smaller first).</summary>
    public double Order { get; init; }
}

/// <summary>
/// One day the habit was done. Its id is <c>habitId@yyyy-MM-dd</c> (<see cref="HabitsStore.CheckId"/>), so the same
/// day ticked on two devices is one record; unticking deletes it.
/// </summary>
public sealed record HabitCheck
{
    public string HabitId { get; init; } = "";

    public DateOnly Day { get; init; }

    public DateTimeOffset CheckedAt { get; init; }
}

/// <summary>
/// Why a habit was skipped for a run of days, written by the user when they came back. Kept for the reports and for AI
/// agents to look for patterns. Its id is <c>habitId@yyyy-MM-dd</c> of <see cref="From"/>.
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

    public static string Clip(string? text, int max)
    {
        var s = (text ?? "").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd();
    }
}
