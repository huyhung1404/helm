using Helm.Core.Settings;

namespace Helm.Modules.Scratch;

/// <summary>
/// Device-local preferences (Scratch itself is synced through <see cref="ScratchStore"/>). Stored in
/// settings/scratch.json on both apps.
/// </summary>
public sealed class ScratchSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>The filter the page opens on (never the trash).</summary>
    public ScratchFilter Filter { get; set; } = ScratchFilter.All;

    /// <summary>Copy what another device sends with Send to clipboard onto this device's clipboard.</summary>
    public bool ReceiveClipboard { get; set; } = true;

    /// <summary>This install, random: it tells its own clipboard signals from the other devices'.</summary>
    public string InstallId { get; set; } = "";
}
