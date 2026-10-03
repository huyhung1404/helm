using Helm.Core.Settings;

namespace Helm.Modules.Missions;

/// <summary>A reminder about today's steps, ready to show as one notification.</summary>
public sealed record MissionReminder(int Count, string Title, string Message);

/// <summary>
/// Decides when to remind about today's step and what to say. Platform-free: the Windows module shows the result as a
/// tray notification, the Android side as a system notification (from its hourly alarm, even with the app closed).
/// At most one reminder per day and device, from <see cref="MissionsSettings.ReminderHour"/>; a mission with progress
/// today (a step done or a tick) is left out, so the reminder only nudges where nothing happened yet.
/// </summary>
public sealed class MissionReminderService
{
    public const int MaxLines = 4;

    private readonly MissionsStore _store;
    private readonly ISettingsStore<MissionsSettings> _settings;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public MissionReminderService(MissionsStore store, ISettingsStoreFactory settings, TimeProvider? time = null)
    {
        _store = store;
        _settings = settings.Get<MissionsSettings>(MissionsIds.ModuleId);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised by <see cref="RemindNow"/>; the platform module shows it.</summary>
    public event EventHandler<MissionReminder>? Requested;

    public ISettingsStore<MissionsSettings> Settings => _settings;

    /// <summary>
    /// The reminder to show now, or null: reminders are off, it is before the reminder hour, today's reminder was
    /// already shown, or no mission in progress is waiting for its step. A returned reminder counts as shown today.
    /// </summary>
    public MissionReminder? TakeDue(TimeZoneInfo? zone = null)
    {
        lock (_gate)
        {
            var s = _settings.Current;
            if (!s.RemindersEnabled) return null;
            zone ??= TimeZoneInfo.Local;
            var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone);
            var today = DateOnly.FromDateTime(local.DateTime);
            if (local.Hour < Math.Clamp(s.ReminderHour, 0, 23) || s.LastReminderDate == today) return null;
            // Nothing waiting: the day is not used up, so a mission started later today still gets its reminder.
            if (Build(_store, _time.GetUtcNow(), zone, skipDoneToday: true) is not { } reminder) return null;
            _settings.Update(x => x.LastReminderDate = today);
            return reminder;
        }
    }

    /// <summary>Shows the reminder right away (to try the notification), whatever the hour; false when nothing waits.</summary>
    public bool RemindNow(TimeZoneInfo? zone = null)
    {
        if (Build(_store, _time.GetUtcNow(), zone ?? TimeZoneInfo.Local, skipDoneToday: false) is not { } reminder) return false;
        Requested?.Invoke(this, reminder);
        return true;
    }

    /// <summary>One line per mission in progress: its current step and, when it is off the plan, by how much.</summary>
    public static MissionReminder? Build(MissionsStore store, DateTimeOffset now, TimeZoneInfo zone, bool skipDoneToday)
    {
        var today = MissionPace.Day(now, zone);
        var lines = new List<string>();
        foreach (var m in store.Missions().Where(m => m.Value.Status == MissionStatus.Active))
        {
            var steps = store.Steps(m.Id).Select(s => s.Value).ToList();
            if (steps.FirstOrDefault(s => !s.IsDone) is not { } current) continue;
            var history = store.History(m.Id);
            if (skipDoneToday && history.Any(e => e.Kind is MissionEventKind.StepCompleted or MissionEventKind.ChecklistTicked && MissionPace.Day(e.At, zone) == today))
                continue;
            var pace = MissionPace.Compute(m.Value, steps, history, now, zone);
            var line = $"{m.Value.Title}: {current.Title}";
            if (pace.DaysOff > 0) line += $" ({MissionsFormat.Pace(pace).ToLowerInvariant()})";
            lines.Add(line);
        }
        if (lines.Count == 0) return null;
        var shown = lines.Take(MaxLines).ToList();
        if (lines.Count > MaxLines) shown.Add($"…and {lines.Count - MaxLines} more");
        var title = lines.Count == 1 ? "Today's step" : $"Today's steps, {lines.Count} missions";
        return new MissionReminder(lines.Count, title, string.Join("\n", shown));
    }
}
