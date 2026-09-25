using System.Text.Json.Serialization;
using Helm.Core.Settings;

namespace Helm.Modules.Zones.Layouts;

/// <summary>zones/layouts.json: the user's custom layouts.</summary>
public sealed class LayoutsFile : IVersionedSettings
{
    public static int CurrentVersion => 2;

    public int Version { get; set; }

    public List<ZoneLayout> Layouts { get; set; } = [];

    /// <summary>
    /// v1 → v2: templates are dropped; custom grid and canvas layouts become custom zone layouts for one monitor and
    /// get numbers 1–9 in order.
    /// </summary>
    public void Migrate(int fromVersion)
    {
        if (fromVersion >= 2) return;
        var migrated = new List<ZoneLayout>();
        foreach (var old in Layouts)
        {
            var zones = old.LegacyKind switch
            {
                "customGrid" => old.LegacyGrid?.ToZones().ToList(),
                "customCanvas" => old.LegacyCanvas,
                _ => null, // templates (focus, columns, rows, grid, priorityGrid) no longer exist
            };
            if (zones is null || zones.Count == 0) continue;
            migrated.Add(new ZoneLayout
            {
                Id = old.Id,
                Name = old.Name,
                Scope = LayoutScope.Monitor,
                Zones = zones,
                Number = migrated.Count < ZoneLayout.MaxNumber ? migrated.Count + 1 : null,
            });
        }
        Layouts = migrated;
    }

    public ZoneLayout? Find(string? id) => id is null ? null : Layouts.FirstOrDefault(l => l.Id == id);

    public ZoneLayout? FindByNumber(int number) => Layouts.FirstOrDefault(l => l.Number == number);
}

/// <summary>Per-monitor choice in zones/applied.json.</summary>
public sealed class AppliedLayout
{
    public string LayoutId { get; set; } = string.Empty;
}

/// <summary>zones/applied.json: monitor id → the layout currently active on it (a spanning layout is listed on each of its monitors).</summary>
public sealed class AppliedFile : IVersionedSettings
{
    public static int CurrentVersion => 2;

    public int Version { get; set; }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, AppliedLayout> Monitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>zones/app-zone-history.json: last zone per app window, for "move newly created windows".</summary>
public sealed class ZoneHistoryFile : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, ZoneHistoryEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ZoneHistoryEntry
{
    public string MonitorId { get; set; } = string.Empty;
    public string LayoutId { get; set; } = string.Empty;
    public List<int> Zones { get; set; } = [];
    public DateTimeOffset Updated { get; set; }
}
