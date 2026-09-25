using System.Text.Json.Serialization;
using Helm.Core.Settings;

namespace Helm.Modules.Zones.Layouts;

/// <summary>zones/layouts.json: template parameters and custom layouts.</summary>
public sealed class LayoutsFile : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    public List<LayoutDefinition> Layouts { get; set; } = [];

    /// <summary>Adds any missing template entries plus the shipped sample custom layouts on first run.</summary>
    public bool EnsureDefaults()
    {
        var changed = false;
        var fresh = Layouts.Count == 0;
        foreach (var template in LayoutTemplates.Defaults())
        {
            if (Layouts.Any(l => l.Id == template.Id)) continue;
            Layouts.Insert(Math.Min(Layouts.Count, (int)template.Kind), template);
            changed = true;
        }

        if (fresh)
        {
            Layouts.AddRange(SampleLayouts());
            changed = true;
        }
        return changed;
    }

    public LayoutDefinition? Find(string? id) => id is null ? null : Layouts.FirstOrDefault(l => l.Id == id);

    public static IEnumerable<LayoutDefinition> SampleLayouts()
    {
        // A wide main area with a narrow sidebar split in two — handy for Unity + browser + terminal.
        yield return new LayoutDefinition
        {
            Id = "sample-main-sidebar",
            Name = "Main + sidebar",
            Kind = LayoutKind.CustomGrid,
            Grid = new GridLayout([0.5, 0.5], [0.7, 0.3], [[0, 1], [0, 2]]),
        };
        // Portrait monitors: a large top area and two stacked bottom zones.
        yield return new LayoutDefinition
        {
            Id = "sample-portrait",
            Name = "Portrait stack",
            Kind = LayoutKind.CustomGrid,
            Grid = new GridLayout([0.5, 0.25, 0.25], [1.0], [[0], [1], [2]]),
        };
        yield return new LayoutDefinition
        {
            Id = "sample-canvas",
            Name = "Floating center",
            Kind = LayoutKind.CustomCanvas,
            Spacing = 0,
            Canvas = [new ZoneRect(0.15, 0.1, 0.7, 0.8), new ZoneRect(0.0, 0.0, 0.3, 0.5), new ZoneRect(0.7, 0.5, 0.3, 0.5)],
        };
    }
}

/// <summary>Per-monitor choice in zones/applied.json.</summary>
public sealed class AppliedLayout
{
    public string LayoutId { get; set; } = LayoutTemplates.TemplateId(LayoutKind.Columns);
    public int Spacing { get; set; } = 16;
    public bool ShowSpacing { get; set; } = true;
}

/// <summary>zones/applied.json: monitor id → applied layout.</summary>
public sealed class AppliedFile : IVersionedSettings
{
    public static int CurrentVersion => 1;

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
