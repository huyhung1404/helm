using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Settings;
using Helm.Modules.WindowZones.Model;

namespace Helm.Modules.WindowZones;

/// <summary>A zone placed on a connected monitor, in physical pixels.</summary>
public sealed record PlacedZone(ZoneDefinition Zone, MonitorInfo Monitor, PixelRect Rect)
{
    public string Id => Zone.Id;
}

/// <summary>
/// Owns window-zones/zones.json (the zones) and window-zones/windows.json (which windows are in them). Thread-safe:
/// the editor and the pages write on the UI thread, the engine reads on its own thread.
/// </summary>
public sealed class WindowZonesData
{
    private readonly object _gate = new();
    private readonly ISettingsStore<ZonesFile> _zones;
    private readonly ISettingsStore<ZoneWindowsFile> _windows;

    public WindowZonesData(ISettingsStoreFactory settings)
    {
        _zones = settings.GetFile<ZonesFile>(Path.Combine("window-zones", "zones.json"));
        _windows = settings.GetFile<ZoneWindowsFile>(Path.Combine("window-zones", "windows.json"));
    }

    /// <summary>Raised (on the writer's thread) after the zones change.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ZoneDefinition> GetZones()
    {
        lock (_gate) return _zones.Current.Zones.Select(z => z.Clone()).ToList();
    }

    /// <summary>The zones of the connected monitors in pixels, in number order (zones without a number last).</summary>
    public IReadOnlyList<PlacedZone> Place(IReadOnlyList<MonitorInfo> monitors) => Place(GetZones(), monitors);

    public static IReadOnlyList<PlacedZone> Place(IEnumerable<ZoneDefinition> zones, IReadOnlyList<MonitorInfo> monitors)
    {
        var placed = new List<PlacedZone>();
        foreach (var zone in zones.OrderBy(z => z.Number ?? int.MaxValue))
        {
            var monitor = monitors.FirstOrDefault(m => string.Equals(m.Id, zone.MonitorId, StringComparison.OrdinalIgnoreCase));
            if (monitor is null) continue;
            placed.Add(new PlacedZone(zone, monitor, ZoneGeometry.ToPixels(zone.Rect, monitor.WorkArea)));
        }
        return placed;
    }

    /// <summary>Replaces every zone (the editor's Save). Kept zones keep their numbers; new ones get free numbers.</summary>
    public void SaveZones(IReadOnlyList<ZoneDefinition> zones, IReadOnlyList<MonitorInfo> monitors)
    {
        var copy = zones.Select(z => z.Clone()).ToList();
        lock (_gate)
        {
            // Zones on monitors that are not connected right now were not in the editor: keep them.
            var connected = monitors.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var offline = _zones.Current.Zones.Where(z => !connected.Contains(z.MonitorId) && copy.All(c => c.Id != z.Id)).Select(z => z.Clone());
            copy.AddRange(offline);
            ZoneNumbers.Assign(copy, monitors.Select(m => m.Id).ToList());
            _zones.Update(f => f.Zones = copy);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Rename(string zoneId, string name)
    {
        lock (_gate)
        {
            if (_zones.Current.Zones.FirstOrDefault(z => z.Id == zoneId) is not { } zone || zone.Name == name.Trim()) return;
            _zones.Update(f => f.Zones.First(z => z.Id == zoneId).Name = name.Trim());
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetNumber(string zoneId, int? number)
    {
        lock (_gate) _zones.Update(f => ZoneNumbers.Set(f.Zones, zoneId, number));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ZoneWindowsFile LoadWindows()
    {
        lock (_gate) return _windows.Current;
    }

    public void SaveWindows(ZoneWindowsFile file)
    {
        lock (_gate) _windows.Update(f => f.Zones = file.Zones);
    }
}
