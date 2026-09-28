using Helm.Core.Settings;

namespace Helm.Modules.Tracker;

/// <summary>A reminder about due items, ready to show as one notification.</summary>
public sealed record TrackerReminder(int Overdue, int DueToday, int DueSoon, string Title, string Message)
{
    public int Total => Overdue + DueToday + DueSoon;
}

/// <summary>
/// Decides when to remind about due items and what to say. Platform-free: the Windows module shows the result as a
/// tray notification, the Android side as a system notification (from its daily alarm, even with the app closed).
/// At most one reminder per day and device; the day's reminder comes from <see cref="TrackerSettings.ReminderHour"/>.
/// </summary>
public sealed class TrackerReminderService
{
    public const int MaxLines = 5;

    private readonly TrackerStore _store;
    private readonly ISettingsStore<TrackerSettings> _settings;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public TrackerReminderService(TrackerStore store, ISettingsStoreFactory settings, TimeProvider? time = null)
    {
        _store = store;
        _settings = settings.Get<TrackerSettings>(TrackerIds.ModuleId);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised by <see cref="RemindNow"/>; the platform module shows it.</summary>
    public event EventHandler<TrackerReminder>? Requested;

    public ISettingsStore<TrackerSettings> Settings => _settings;

    /// <summary>
    /// The reminder to show now, or null: reminders are off, it is before the reminder hour, today's reminder was
    /// already shown, or nothing is due. A returned reminder counts as shown today.
    /// </summary>
    public TrackerReminder? TakeDue(TimeZoneInfo? zone = null)
    {
        lock (_gate)
        {
            var s = _settings.Current;
            if (!s.RemindersEnabled) return null;
            var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone ?? TimeZoneInfo.Local);
            var today = DateOnly.FromDateTime(local.DateTime);
            if (local.Hour < Math.Clamp(s.ReminderHour, 0, 23) || s.LastReminderDate == today) return null;

            var reminder = Build(_store, today, s.RemindDaysBefore);
            // Nothing due: keep checking, so an item added later today can still be reminded about.
            if (reminder is null) return null;
            _settings.Update(x => x.LastReminderDate = today);
            return reminder;
        }
    }

    /// <summary>"Remind me now": shows the reminder whatever the hour. False when nothing is due.</summary>
    public bool RemindNow(TimeZoneInfo? zone = null)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone ?? TimeZoneInfo.Local).DateTime);
        if (Build(_store, today, _settings.Current.RemindDaysBefore) is not { } reminder) return false;
        Requested?.Invoke(this, reminder);
        return true;
    }

    /// <summary>The next local time the daily check should run (today's reminder hour, or tomorrow's if past).</summary>
    public DateTimeOffset NextCheck(TimeZoneInfo? zone = null)
    {
        var tz = zone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), tz);
        var at = new DateTime(local.Year, local.Month, local.Day, Math.Clamp(_settings.Current.ReminderHour, 0, 23), 0, 0);
        if (at <= local.DateTime) at = at.AddDays(1);
        return new DateTimeOffset(at, tz.GetUtcOffset(at));
    }

    /// <summary>What is due: open items of every workspace with a due date on or before today + <paramref name="daysBefore"/>.</summary>
    public static TrackerReminder? Build(TrackerStore store, DateOnly today, int daysBefore)
    {
        var horizon = today.AddDays(Math.Clamp(daysBefore, 0, 30));
        var workspaces = store.Workspaces().ToDictionary(w => w.Id, w => w.Value, StringComparer.Ordinal);
        var due = store.AllItems()
            .Where(i => !i.Value.IsCompleted && i.Value.DueDate is { } d && d <= horizon && workspaces.ContainsKey(i.Value.WorkspaceId))
            .OrderBy(i => i.Value.DueDate)
            .ThenByDescending(i => i.Value.Priority)
            .ThenBy(i => i.Value.CreatedAt)
            .ToList();
        if (due.Count == 0) return null;

        int overdue = 0, dueToday = 0, soon = 0;
        foreach (var (_, item, _) in due)
        {
            if (item.DueDate < today) overdue++;
            else if (item.DueDate == today) dueToday++;
            else soon++;
        }

        var parts = new List<string>();
        if (overdue > 0) parts.Add($"{overdue} overdue");
        if (dueToday > 0) parts.Add($"{dueToday} due today");
        if (soon > 0) parts.Add($"{soon} due soon");
        var title = "Tracker: " + string.Join(" · ", parts);

        var lines = due.Take(MaxLines).Select(i =>
        {
            var item = i.Value;
            var name = workspaces[item.WorkspaceId].Kind == WorkspaceKind.Debts && item.Person.Length > 0
                ? (item.Title.Length > 0 ? $"{item.Person} — {item.Title}" : item.Person)
                : item.Title;
            return $"{name} ({TrackerFormat.Due(item.DueDate!.Value, today).ToLowerInvariant()})";
        }).ToList();
        if (due.Count > MaxLines) lines.Add($"and {due.Count - MaxLines} more");
        return new TrackerReminder(overdue, dueToday, soon, title, string.Join("\n", lines));
    }
}
