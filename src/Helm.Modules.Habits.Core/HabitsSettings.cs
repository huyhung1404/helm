using Helm.Core.Settings;

namespace Helm.Modules.Habits;

/// <summary>
/// Device-local preferences (the habits themselves are synced through <see cref="HabitsStore"/>). Stored in
/// settings/habits.json on both apps.
/// </summary>
public sealed class HabitsSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>Confetti when a habit is done for the day.</summary>
    public bool PlayCelebrations { get; set; } = true;

    /// <summary>A daily notification about habits skipped for days in a row.</summary>
    public bool RemindersEnabled { get; set; } = true;

    /// <summary>Local hour (0–23) from which the day's reminder may be shown.</summary>
    public int ReminderHour { get; set; } = 20;

    /// <summary>The local date of the last reminder shown on this device (one per day).</summary>
    public DateOnly? LastReminderDate { get; set; }
}
