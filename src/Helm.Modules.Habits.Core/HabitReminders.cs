using Helm.Core.Settings;

namespace Helm.Modules.Habits;

/// <summary>What a reminder is about: habits skipped for days, or streaks still waiting for today.</summary>
public enum HabitReminderKind
{
    Skipped,
    Streak,
}

/// <summary>A reminder about habits, ready to show as one notification.</summary>
public sealed record HabitReminder(int Count, string Title, string Message, HabitReminderKind Kind = HabitReminderKind.Skipped);

/// <summary>
/// Decides when to remind about habits, and what to say. Two reminders, each at most once a day and device: habits
/// skipped for <see cref="HabitLapses.MinDays"/> days or more in a row (from <see cref="HabitsSettings.ReminderHour"/>),
/// and in the evening habits whose streak of <see cref="MinStreak"/> days or more is not done yet today (from
/// <see cref="HabitsSettings.StreakReminderHour"/>). A habit already done today is left out of both. Platform-free: the
/// Windows module shows the result as a tray notification, the Android side as a system notification (from its hourly
/// alarm, even with the app closed).
/// </summary>
public sealed class HabitReminderService
{
    public const int MaxLines = 4;

    /// <summary>A streak shorter than this is not worth a notification.</summary>
    public const int MinStreak = 2;

    private readonly HabitsStore _store;
    private readonly ISettingsStore<HabitsSettings> _settings;
    private readonly object _gate = new();

    public HabitReminderService(HabitsStore store, ISettingsStoreFactory settings)
    {
        _store = store;
        _settings = settings.Get<HabitsSettings>(HabitsIds.ModuleId);
    }

    /// <summary>Raised by <see cref="RemindNow"/>; the platform module shows it.</summary>
    public event EventHandler<HabitReminder>? Requested;

    public ISettingsStore<HabitsSettings> Settings => _settings;

    /// <summary>
    /// The reminder to show now, or null: reminders are off, it is before the reminder hour, today's reminder was
    /// already shown, or no habit is slipping. A returned reminder counts as shown today.
    /// </summary>
    public HabitReminder? TakeDue()
    {
        lock (_gate)
        {
            var s = _settings.Current;
            var local = TimeZoneInfo.ConvertTime(_store.Now, _store.Zone);
            var today = DateOnly.FromDateTime(local.DateTime);
            if (s.RemindersEnabled && local.Hour >= Math.Clamp(s.ReminderHour, 0, 23) && s.LastReminderDate != today
                // Nothing slipping: the day is not used up, so a habit missed by tonight still gets its reminder.
                && Build(_store) is { } skipped)
            {
                _settings.Update(x => x.LastReminderDate = today);
                return skipped;
            }
            // The evening guard comes after: a habit a streak is built on, still open late in the day.
            if (s.StreakRemindersEnabled && local.Hour >= Math.Clamp(s.StreakReminderHour, 0, 23) && s.LastStreakReminderDate != today
                && BuildStreak(_store) is { } streak)
            {
                _settings.Update(x => x.LastStreakReminderDate = today);
                return streak;
            }
            return null;
        }
    }

    /// <summary>Shows a reminder right away (to try the notification), whatever the hour; false when nothing is due.</summary>
    public bool RemindNow()
    {
        if ((Build(_store) ?? BuildStreak(_store)) is not { } reminder) return false;
        Requested?.Invoke(this, reminder);
        return true;
    }

    /// <summary>
    /// A habit to build with a streak of <see cref="MinStreak"/> days or more that is not done yet today: the chain breaks
    /// at midnight. Habits to quit are left out (their day is ticked the day after, and what someone quits is private).
    /// </summary>
    public static HabitReminder? BuildStreak(HabitsStore store)
    {
        var today = store.Today;
        var done = store.AllDoneDays();
        var risky = new List<(string Title, int Streak)>();
        foreach (var h in store.Habits())
        {
            if (h.Value.Kind != HabitKind.Build) continue;
            var days = done.TryGetValue(h.Id, out var d) ? d : new HashSet<DateOnly>();
            if (days.Contains(today)) continue;
            var streak = HabitStats.CurrentStreak(days, today);
            if (streak >= MinStreak) risky.Add((h.Value.Title, streak));
        }
        if (risky.Count == 0) return null;
        risky.Sort((a, b) => b.Streak.CompareTo(a.Streak));
        var lines = risky.Take(MaxLines).Select(r => $"{r.Title}: {r.Streak}-day streak").ToList();
        if (risky.Count > MaxLines) lines.Add($"…and {risky.Count - MaxLines} more");
        var title = risky.Count == 1 ? "Keep your streak alive" : $"{risky.Count} streaks end tonight";
        return new HabitReminder(risky.Count, title, string.Join("\n", lines) + "\nDone today keeps the chain going. Even part of it counts.",
            HabitReminderKind.Streak);
    }

    /// <summary>
    /// One line per habit not done for its action day and skipped for days in a row. A habit to quit is never named: the
    /// notification can show on a locked screen, and what someone is quitting is private.
    /// </summary>
    public static HabitReminder? Build(HabitsStore store)
    {
        var today = store.Today;
        var done = store.AllDoneDays();
        var lines = new List<string>();
        var toBuild = false;
        foreach (var h in store.Habits())
        {
            var days = done.TryGetValue(h.Id, out var d) ? d : new HashSet<DateOnly>();
            if (days.Contains(HabitDays.ActionDay(h.Value, today))) continue;
            var missed = HabitStats.MissedRun(h.Value, days, today);
            if (missed < HabitLapses.MinDays) continue;
            if (h.Value.Kind == HabitKind.Quit) lines.Add($"A habit you are quitting: not marked for {missed} days");
            else
            {
                lines.Add($"{h.Value.Title}: skipped for {missed} days");
                toBuild = true;
            }
        }
        if (lines.Count == 0) return null;
        var shown = lines.Take(MaxLines).ToList();
        if (lines.Count > MaxLines) shown.Add($"…and {lines.Count - MaxLines} more");
        var title = lines.Count == 1 ? "A habit is slipping" : $"{lines.Count} habits are slipping";
        var footer = toBuild ? "Do it today to start a new streak." : "Open Habits to mark the days you stayed clean.";
        return new HabitReminder(lines.Count, title, string.Join("\n", shown) + "\n" + footer);
    }
}
