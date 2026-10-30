using Helm.Core.Settings;

namespace Helm.Modules.Habits;

/// <summary>A reminder about slipping habits, ready to show as one notification.</summary>
public sealed record HabitReminder(int Count, string Title, string Message);

/// <summary>
/// Decides when to remind about habits skipped for <see cref="HabitLapses.MinDays"/> days or more in a row, and what to
/// say. Platform-free: the Windows module shows the result as a tray notification, the Android side as a system
/// notification (from its hourly alarm, even with the app closed). At most one reminder per day and device, from
/// <see cref="HabitsSettings.ReminderHour"/>; a habit already done today is left out.
/// </summary>
public sealed class HabitReminderService
{
    public const int MaxLines = 4;

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
            if (!s.RemindersEnabled) return null;
            var local = TimeZoneInfo.ConvertTime(_store.Now, _store.Zone);
            var today = DateOnly.FromDateTime(local.DateTime);
            if (local.Hour < Math.Clamp(s.ReminderHour, 0, 23) || s.LastReminderDate == today) return null;
            // Nothing slipping: the day is not used up, so a habit missed by tonight still gets its reminder.
            if (Build(_store) is not { } reminder) return null;
            _settings.Update(x => x.LastReminderDate = today);
            return reminder;
        }
    }

    /// <summary>Shows the reminder right away (to try the notification), whatever the hour; false when nothing slips.</summary>
    public bool RemindNow()
    {
        if (Build(_store) is not { } reminder) return false;
        Requested?.Invoke(this, reminder);
        return true;
    }

    /// <summary>One line per habit not done today and skipped for days in a row.</summary>
    public static HabitReminder? Build(HabitsStore store)
    {
        var today = store.Today;
        var done = store.AllDoneDays();
        var lines = new List<string>();
        foreach (var h in store.Habits())
        {
            var days = done.TryGetValue(h.Id, out var d) ? d : new HashSet<DateOnly>();
            if (days.Contains(today)) continue;
            var missed = HabitStats.MissedRun(h.Value, days, today);
            if (missed >= HabitLapses.MinDays) lines.Add($"{h.Value.Title}: skipped for {missed} days");
        }
        if (lines.Count == 0) return null;
        var shown = lines.Take(MaxLines).ToList();
        if (lines.Count > MaxLines) shown.Add($"…and {lines.Count - MaxLines} more");
        var title = lines.Count == 1 ? "A habit is slipping" : $"{lines.Count} habits are slipping";
        return new HabitReminder(lines.Count, title, string.Join("\n", shown) + "\nDo it today to start a new streak.");
    }
}
