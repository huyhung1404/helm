using System.Text.Json.Serialization;

namespace Helm.Modules.Zones.Layouts;

public enum LayoutScope
{
    /// <summary>Zones are fractions of one monitor's work area; the layout can be applied to any monitor.</summary>
    Monitor,

    /// <summary>Zones are fractions of the combined work area of <see cref="ZoneLayout.MonitorIds"/>; zones may cross monitors.</summary>
    Span,
}

/// <summary>A user-drawn zone layout (layouts.json). There are no built-in templates.</summary>
public sealed class ZoneLayout
{
    public const int MaxNumber = 9;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Layout";

    /// <summary>1–9: Ctrl+Win+Alt+&lt;number&gt; switches the monitor under the cursor to this layout. Null = no shortcut.</summary>
    public int? Number { get; set; }

    public LayoutScope Scope { get; set; } = LayoutScope.Monitor;

    /// <summary>Span: the monitors the layout covers. Monitor: the monitor it was drawn on (informational).</summary>
    public List<string> MonitorIds { get; set; } = [];

    /// <summary>Zones as fractions of the layout's reference rectangle, in zone-number order.</summary>
    public List<ZoneRect> Zones { get; set; } = [];

    // ── v1 fields, read only to migrate old layouts.json files (never written) ──
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyKind { get; set; }

    [JsonPropertyName("grid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GridLayout? LegacyGrid { get; set; }

    [JsonPropertyName("canvas")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ZoneRect>? LegacyCanvas { get; set; }

    public ZoneLayout Clone() => new()
    {
        Id = Id,
        Name = Name,
        Number = Number,
        Scope = Scope,
        MonitorIds = MonitorIds.ToList(),
        Zones = Zones.ToList(),
    };

    public string ScopeText => Scope == LayoutScope.Span ? $"Across {MonitorIds.Count} monitors" : "One monitor";
}
