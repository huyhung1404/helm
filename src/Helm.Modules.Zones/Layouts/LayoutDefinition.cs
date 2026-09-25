using System.Text.Json.Serialization;

namespace Helm.Modules.Zones.Layouts;

public enum LayoutKind
{
    Focus,
    Columns,
    Rows,
    Grid,
    PriorityGrid,
    CustomGrid,
    CustomCanvas,
}

/// <summary>A template or custom layout as stored in layouts.json.</summary>
public sealed class LayoutDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Layout";

    public LayoutKind Kind { get; set; }

    /// <summary>Templates only: number of zones to generate.</summary>
    public int ZoneCount { get; set; } = 3;

    /// <summary>Gap between zones in DIP-like pixels (scaled by monitor DPI). Default for monitors applying this layout.</summary>
    public int Spacing { get; set; } = 16;

    public bool ShowSpacing { get; set; } = true;

    /// <summary>How close (px) the cursor must be to a zone for it to highlight when between zones.</summary>
    public int SensitivityRadius { get; set; } = 20;

    public GridLayout? Grid { get; set; }

    public List<ZoneRect>? Canvas { get; set; }

    [JsonIgnore]
    public bool IsTemplate => Kind is not (LayoutKind.CustomGrid or LayoutKind.CustomCanvas);

    /// <summary>Grid-like layouts get spacing between zones; canvas and focus zones are placed as drawn.</summary>
    [JsonIgnore]
    public bool UsesSpacing => Kind is not (LayoutKind.Focus or LayoutKind.CustomCanvas);

    public LayoutDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        Kind = Kind,
        ZoneCount = ZoneCount,
        Spacing = Spacing,
        ShowSpacing = ShowSpacing,
        SensitivityRadius = SensitivityRadius,
        Grid = Grid?.Clone(),
        Canvas = Canvas?.ToList(),
    };

    /// <summary>Zones of this layout as fractions of the work area, in zone-index order.</summary>
    public IReadOnlyList<ZoneRect> GetZones() => Kind switch
    {
        LayoutKind.CustomGrid => (Grid ?? GridLayout.Single()).ToZones(),
        LayoutKind.CustomCanvas => Canvas ?? [],
        _ => LayoutTemplates.Generate(Kind, ZoneCount),
    };

    /// <summary>The grid form of this layout (templates other than Focus, and custom grids); null for canvas/focus.</summary>
    public GridLayout? AsGrid() => Kind switch
    {
        LayoutKind.CustomGrid => Grid ?? GridLayout.Single(),
        LayoutKind.Focus or LayoutKind.CustomCanvas => null,
        _ => LayoutTemplates.GenerateGrid(Kind, ZoneCount),
    };
}
