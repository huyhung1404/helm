using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Modules.WindowZones.Engine;

namespace Helm.Modules.WindowZones;

/// <summary>Window Zones' ids, for the code that does not know the Windows module.</summary>
public static class WindowZonesIds
{
    /// <summary>The module's id: its settings file and its enabled state.</summary>
    public const string ModuleId = "window-zones";
}

/// <summary>
/// The tool as its pages see it (on Windows: WindowZonesModule): its settings and zones, the windows in them right now,
/// and what the pages may ask of the engine. Every action is a no-op while the tool is off.
/// </summary>
public interface IWindowZones : IModule
{
    ISettingsStore<WindowZonesSettings> Settings { get; }

    WindowZonesData Data { get; }

    /// <summary>Every zone and the windows in it right now (no windows while the tool is off).</summary>
    IReadOnlyList<ZoneSnapshot> Zones { get; }

    /// <summary>Raised on a background thread whenever <see cref="Zones"/> changes.</summary>
    event EventHandler? ZonesChanged;

    /// <summary>The full-screen editor on every monitor.</summary>
    void OpenEditor();

    void OpenZone(string zoneId);

    void CloseZone(string zoneId);

    /// <summary>Lights up the zone on its screen for a moment.</summary>
    void ShowZone(string zoneId);

    /// <summary>Brings the window to the front, in its zone.</summary>
    void ShowWindow(nint handle);

    /// <summary>Takes the window out of its zone (it stays where it is).</summary>
    void Release(nint handle);
}
