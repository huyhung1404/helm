using Helm.Core.Settings;

namespace Helm.Modules.WindowZones.Model;

/// <summary>One zone the user drew: a rectangle on one monitor, with a number for its shortcut and a name.</summary>
public sealed class ZoneDefinition
{
    /// <summary>1–9: the zone's shortcut (Ctrl+Alt+number by default). Zones beyond nine have none.</summary>
    public const int MaxNumber = 9;

    /// <summary>Stable across edits, so the windows in a zone stay with it when it is moved or resized.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int? Number { get; set; }

    /// <summary>Optional; shown with the number ("2 · Browser"). Empty means just the number.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The monitor the zone is on (<see cref="Helm.Core.Desktop.MonitorInfo.Id"/>). Off while it is unplugged.</summary>
    public string MonitorId { get; set; } = string.Empty;

    /// <summary>Fractions of that monitor's work area, so the zone follows resolution and taskbar changes.</summary>
    public ZoneRect Rect { get; set; }

    public string Label => string.IsNullOrWhiteSpace(Name)
        ? Number is { } n ? $"Zone {n}" : "Zone"
        : Number is { } m ? $"{m} · {Name.Trim()}" : Name.Trim();

    public ZoneDefinition Clone() => new() { Id = Id, Number = Number, Name = Name, MonitorId = MonitorId, Rect = Rect };
}

/// <summary>window-zones/zones.json: the zones the user drew, on every monitor. Device-specific (never synced).</summary>
public sealed class ZonesFile : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    public List<ZoneDefinition> Zones { get; set; } = [];
}

/// <summary>A window remembered in a zone. Window handles outlive a restart of Helm, not of the program.</summary>
public sealed class ZoneMemberRecord
{
    public long Handle { get; set; }

    /// <summary>Checked on load so a recycled handle of another program is never taken for the window.</summary>
    public int ProcessId { get; set; }

    /// <summary>Its size before it was dropped into the zone, given back when it is dragged out.</summary>
    public int OriginalWidth { get; set; }

    public int OriginalHeight { get; set; }
}

public sealed class ZoneMembersRecord
{
    public List<ZoneMemberRecord> Windows { get; set; } = [];

    /// <summary>The window last in front in this zone: it comes back on top when the zone opens.</summary>
    public long LastActive { get; set; }
}

/// <summary>
/// window-zones/windows.json: which windows are in which zone, so a closed zone (its windows minimized) still opens
/// after Helm restarts, for an update for example.
/// </summary>
public sealed class ZoneWindowsFile : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    [System.Text.Json.Serialization.JsonObjectCreationHandling(System.Text.Json.Serialization.JsonObjectCreationHandling.Populate)]
    public Dictionary<string, ZoneMembersRecord> Zones { get; set; } = new(StringComparer.Ordinal);
}
