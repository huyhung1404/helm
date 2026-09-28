using Helm.Core.Settings;

namespace Helm.Modules.Tracker;

/// <summary>
/// Device-local view preferences (the data itself is synced through <see cref="TrackerStore"/>). Stored in
/// settings/tracker.json on both apps.
/// </summary>
public sealed class TrackerSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>The workspace the page opens on; null picks the first one.</summary>
    public string? SelectedWorkspaceId { get; set; }

    public bool ShowCompleted { get; set; } = true;

    public ReportRange ReportRange { get; set; } = ReportRange.Last7Days;

    /// <summary>The report covers every workspace instead of the selected one.</summary>
    public bool ReportAllWorkspaces { get; set; }

    /// <summary>A daily notification about open items that are due soon or overdue.</summary>
    public bool RemindersEnabled { get; set; } = true;

    /// <summary>Local hour (0–23) from which the day's reminder may be shown.</summary>
    public int ReminderHour { get; set; } = 9;

    /// <summary>How many days before the due date an item counts as "due soon" (0 = only on the day).</summary>
    public int RemindDaysBefore { get; set; } = 1;

    /// <summary>The local date of the last reminder shown on this device (one per day).</summary>
    public DateOnly? LastReminderDate { get; set; }
}
