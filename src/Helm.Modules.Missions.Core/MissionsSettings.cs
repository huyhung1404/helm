using Helm.Core.Settings;

namespace Helm.Modules.Missions;

/// <summary>
/// Device-local preferences (the missions themselves are synced through <see cref="MissionsStore"/>). Stored in
/// settings/missions.json on both apps.
/// </summary>
public sealed class MissionsSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>Confetti when a phase or the whole mission is done.</summary>
    public bool PlayCelebrations { get; set; } = true;

    /// <summary>Ask before completing a step whose checklist is not all ticked.</summary>
    public bool ConfirmUntickedChecklist { get; set; } = true;

    /// <summary>List completed and abandoned missions in the picker.</summary>
    public bool ShowFinishedMissions { get; set; } = true;

    /// <summary>The mission the page opens on; null picks the first one in progress.</summary>
    public string? SelectedMissionId { get; set; }

    /// <summary>Finished missions whose celebration was already shown on this device (finished elsewhere: shown once here).</summary>
    public List<string> CelebratedMissionIds { get; set; } = [];
}
