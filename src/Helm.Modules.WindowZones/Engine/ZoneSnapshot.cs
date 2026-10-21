using Helm.Core.Desktop;
using Helm.Core.Geometry;

namespace Helm.Modules.WindowZones.Engine;

/// <summary>A window in a zone, as the pages and the tab strip show it.</summary>
public sealed record ZoneWindowSnapshot(nint Handle, string Title, string ProcessName, string ProcessPath, bool IsMinimized, bool IsTop);

/// <summary>A zone and its windows right now (for the content page).</summary>
public sealed record ZoneSnapshot(
    string Id,
    int? Number,
    string Name,
    string Label,
    string MonitorName,
    bool IsConnected,
    IReadOnlyList<ZoneWindowSnapshot> Windows)
{
    /// <summary>Has windows and every one of them is minimized.</summary>
    public bool IsClosed => Windows.Count > 0 && Windows.All(w => w.IsMinimized);
}

/// <summary>What the tab strip shows: the zone's windows across its top edge, <see cref="Current"/> highlighted.</summary>
public sealed record TabStripView(string Label, PixelRect Zone, MonitorInfo Monitor, IReadOnlyList<ZoneWindowSnapshot> Windows, nint Current);
