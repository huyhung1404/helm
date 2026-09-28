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
}
