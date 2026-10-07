using Helm.Core.Settings;

namespace Helm.Modules.Stash;

/// <summary>
/// Device-local preferences (the stash itself is synced through <see cref="StashStore"/>). Stored in
/// settings/stash.json on both apps.
/// </summary>
public sealed class StashSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>The filter the page opens on (never the trash).</summary>
    public StashFilter Filter { get; set; } = StashFilter.All;

    /// <summary>Copy what another device sends with Send to clipboard onto this device's clipboard.</summary>
    public bool ReceiveClipboard { get; set; } = true;

    /// <summary>This install, random: it tells its own clipboard signals from the other devices'.</summary>
    public string InstallId { get; set; } = "";
}
