using Helm.Core.Settings;
using Helm.Modules.Wallet;

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
/// Debts due in Wallet's debt book are in the same reminder (they were in the Tracker until Helm 0.26).
/// </summary>
public sealed class TrackerReminderService
{
    public const int MaxLines = 5;

    private readonly TrackerStore _store;
    private readonly DebtBook? _debts;
    private readonly TrackerDebtHandover? _handover;
    private readonly ISettingsStore<TrackerSettings> _settings;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public TrackerReminderService(TrackerStore store, ISettingsStoreFactory settings, TimeProvider? time = null, DebtBook? debts = null)
    {
        _store = store;
        _debts = debts;
        _handover = debts is null ? null : new TrackerDebtHandover(store, debts);
        _settings = settings.Get<TrackerSettings>(TrackerIds.ModuleId);
        _time = time ?? TimeProvider.System;
        MoveOldDebts();
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
        MakeTodaysRepeats();
        lock (_gate)
        {
            var s = _settings.Current;
            if (!s.RemindersEnabled) return null;
            var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone ?? TimeZoneInfo.Local);
            var today = DateOnly.FromDateTime(local.DateTime);
            if (local.Hour < Math.Clamp(s.ReminderHour, 0, 23) || s.LastReminderDate == today) return null;

            var reminder = Build(_store, today, s.RemindDaysBefore, _debts);
            // Nothing due: keep checking, so an item added later today can still be reminded about.
            if (reminder is null) return null;
            _settings.Update(x => x.LastReminderDate = today);
            return reminder;
        }
    }

    /// <summary>
    /// Called on the reminder timer (PC every 10 minutes, Android every hour): also makes today's repeating tasks and
    /// copies into Wallet any debt an older Helm added to the old debt book meanwhile.
    /// </summary>
    private void MakeTodaysRepeats()
    {
        try { _store.EnsureRepeats(); }
        catch (Exception) { /* a sync store problem must not stop the reminder */ }
        MoveOldDebts();
    }

    /// <summary>Copies the old debt book into Wallet (see <see cref="TrackerDebtHandover"/>).</summary>
    public int MoveOldDebts()
    {
        try { return _handover?.Run() ?? 0; }
        catch (Exception) { return 0; /* tried again on the next check */ }
    }

    /// <summary>"Remind me now": shows the reminder whatever the hour. False when nothing is due.</summary>

    public bool RemindNow(TimeZoneInfo? zone = null)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone ?? TimeZoneInfo.Local).DateTime);
        if (Build(_store, today, _settings.Current.RemindDaysBefore, _debts) is not { } reminder) return false;
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

    /// <summary>
    /// What is due: open items of every to-do list with a due date on or before today + <paramref name="daysBefore"/>,
    /// and the people in the debt book whose balance is due by then.
    /// </summary>
    public static TrackerReminder? Build(TrackerStore store, DateOnly today, int daysBefore, DebtBook? debts = null)
    {
        var horizon = today.AddDays(Math.Clamp(daysBefore, 0, 30));
        var zone = TimeZoneInfo.Local;
        var due = store.AllItems()
            .Where(i => !i.Value.IsCompleted && i.Value.DueDate is { } d && d <= horizon)
            .OrderBy(i => i.Value.DueDate)
            .ThenByDescending(i => i.Value.Priority)
            .ThenBy(i => i.Value.CreatedAt)
            .Select(i => (Day: i.Value.DueDate!.Value, Name: i.Value.Title))
            .ToList();
        if (debts is not null)
        {
            foreach (var person in debts.Open().Where(p => !p.IsSettled))
            {
                if (DebtDue.Day(person.Due(zone), zone) is not { } day || day > horizon) continue;
                var money = WalletFormat.Money(Math.Abs(person.Balance));
                due.Add((day, person.Balance > 0 ? $"{person.Name} pays back {money}" : $"Pay {person.Name} back {money}"));
            }
            due = due.OrderBy(d => d.Day).ToList();
        }
        if (due.Count == 0) return null;

        int overdue = 0, dueToday = 0, soon = 0;
        foreach (var (day, _) in due)
        {
            if (day < today) overdue++;
            else if (day == today) dueToday++;
            else soon++;
        }

        var parts = new List<string>();
        if (overdue > 0) parts.Add($"{overdue} overdue");
        if (dueToday > 0) parts.Add($"{dueToday} due today");
        if (soon > 0) parts.Add($"{soon} due soon");
        var title = "Tracker: " + string.Join(" · ", parts);

        var lines = due.Take(MaxLines).Select(d => $"{d.Name} ({TrackerFormat.Due(d.Day, today).ToLowerInvariant()})").ToList();
        if (due.Count > MaxLines) lines.Add($"and {due.Count - MaxLines} more");
        return new TrackerReminder(overdue, dueToday, soon, title, string.Join("\n", lines));
    }
}
