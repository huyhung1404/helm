using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Settings;
using Helm.Modules.Zones.Layouts;

namespace Helm.Modules.Zones;

/// <summary>
/// The zones active around one monitor, ready for hit testing (physical pixels). For a spanning layout the zones and
/// <see cref="Reference"/> extend over every covered monitor.
/// </summary>
public sealed record MonitorZones(ZoneLayout Layout, IReadOnlyList<MonitorInfo> Monitors, PixelRect Reference, IReadOnlyList<PixelRect> Zones)
{
    /// <summary>Stable key for "which zone set is this window in" (layout + where it is applied).</summary>
    public string Key => $"{Layout.Id}@{string.Join("+", Monitors.Select(m => m.Id))}";

    public MonitorInfo PrimaryMonitor => Monitors[0];
}

/// <summary>
/// Owns layouts.json, applied.json and app-zone-history.json. Thread-safe: the editor (UI thread) writes, the
/// snapping engine (Zones thread) reads through <see cref="GetZones"/>.
/// </summary>
public sealed class ZonesDataService
{
    private readonly object _gate = new();
    private readonly ISettingsStore<LayoutsFile> _layouts;
    private readonly ISettingsStore<AppliedFile> _applied;
    private readonly ISettingsStore<ZoneHistoryFile> _history;

    public ZonesDataService(ISettingsStoreFactory settings)
    {
        _layouts = settings.GetFile<LayoutsFile>(Path.Combine("zones", "layouts.json"));
        _applied = settings.GetFile<AppliedFile>(Path.Combine("zones", "applied.json"));
        _history = settings.GetFile<ZoneHistoryFile>(Path.Combine("zones", "app-zone-history.json"));

        // Nothing to snap to before the user draws a layout: start with an editable two-column layout.
        if (_layouts.Current.Layouts.Count == 0) _layouts.Update(f => f.Layouts.Add(LayoutGeometry.Starter(null)));
        // Drop applied entries that point at layouts which no longer exist (e.g. removed templates).
        var valid = _layouts.Current.Layouts.Select(l => l.Id).ToHashSet();
        if (_applied.Current.Monitors.Values.Any(a => !valid.Contains(a.LayoutId)))
            _applied.Update(f =>
            {
                foreach (var key in f.Monitors.Where(kv => !valid.Contains(kv.Value.LayoutId)).Select(kv => kv.Key).ToList())
                    f.Monitors.Remove(key);
            });
    }

    /// <summary>Raised (on the writer's thread) after layouts or applied layouts change.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ZoneLayout> GetLayouts()
    {
        lock (_gate) return _layouts.Current.Layouts.Select(l => l.Clone()).ToList();
    }

    public ZoneLayout? GetLayout(string id)
    {
        lock (_gate) return _layouts.Current.Find(id)?.Clone();
    }

    /// <summary>Id of the layout active on <paramref name="monitor"/> (the first layout when none was chosen).</summary>
    public ZoneLayout? GetAppliedLayout(MonitorInfo monitor)
    {
        lock (_gate)
        {
            var file = _layouts.Current;
            if (_applied.Current.Monitors.TryGetValue(monitor.Id, out var applied) && file.Find(applied.LayoutId) is { } layout)
                return layout.Clone();
            return file.Layouts.FirstOrDefault(l => l.Scope == LayoutScope.Monitor)?.Clone();
        }
    }

    /// <summary>Pixel zones active around <paramref name="monitor"/>, or null when it has no usable layout.</summary>
    public MonitorZones? GetZones(MonitorInfo monitor, IReadOnlyList<MonitorInfo> monitors)
    {
        var layout = GetAppliedLayout(monitor);
        if (layout is null || layout.Zones.Count == 0) return null;
        if (LayoutGeometry.ReferenceRect(layout, monitor, monitors) is not { } reference) return null;
        var covered = LayoutGeometry.CoveredMonitors(layout, monitor, monitors);
        return new MonitorZones(layout, covered, reference, LayoutGeometry.ToPixels(layout.Zones, reference));
    }

    /// <summary>Makes <paramref name="layout"/> active on <paramref name="target"/> (and on every monitor it spans).</summary>
    public void Apply(ZoneLayout layout, MonitorInfo target, IReadOnlyList<MonitorInfo> monitors)
    {
        lock (_gate)
        {
            var covered = LayoutGeometry.CoveredMonitors(layout, target, monitors);
            _applied.Update(f =>
            {
                foreach (var m in covered) f.Monitors[m.Id] = new AppliedLayout { LayoutId = layout.Id };
            });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+Win+Alt+&lt;number&gt;: applies the layout with that number around <paramref name="target"/>.</summary>
    public ZoneLayout? ApplyNumber(int number, MonitorInfo target, IReadOnlyList<MonitorInfo> monitors)
    {
        ZoneLayout? layout;
        lock (_gate) layout = _layouts.Current.FindByNumber(number)?.Clone();
        if (layout is null) return null;
        if (layout.Scope == LayoutScope.Span && !LayoutGeometry.CoveredMonitors(layout, target, monitors).Any(m => m.Id == target.Id))
            return null; // a spanning layout only applies where its monitors are
        Apply(layout, target, monitors);
        return layout;
    }

    /// <summary>Adds or replaces a layout. A number already used by another layout is taken over by this one.</summary>
    public void SaveLayout(ZoneLayout layout)
    {
        var copy = layout.Clone();
        lock (_gate)
        {
            _layouts.Update(f =>
            {
                if (copy.Number is { } n)
                    foreach (var other in f.Layouts.Where(l => l.Id != copy.Id && l.Number == n)) other.Number = null;
                var index = f.Layouts.FindIndex(l => l.Id == copy.Id);
                if (index >= 0) f.Layouts[index] = copy;
                else f.Layouts.Add(copy);
            });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteLayout(string id)
    {
        lock (_gate)
        {
            _layouts.Update(f => f.Layouts.RemoveAll(l => l.Id == id));
            _applied.Update(f =>
            {
                foreach (var key in f.Monitors.Where(kv => kv.Value.LayoutId == id).Select(kv => kv.Key).ToList())
                    f.Monitors.Remove(key);
            });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordHistory(IEnumerable<string> keys, ZoneHistoryEntry entry)
    {
        lock (_gate)
        {
            _history.Update(f =>
            {
                foreach (var key in keys) f.Entries[key] = entry;
                if (f.Entries.Count > 500)
                    foreach (var old in f.Entries.OrderBy(kv => kv.Value.Updated).Take(f.Entries.Count - 500).Select(kv => kv.Key).ToList())
                        f.Entries.Remove(old);
            });
        }
    }

    public ZoneHistoryEntry? FindHistory(IEnumerable<string> keys)
    {
        lock (_gate)
        {
            foreach (var key in keys)
                if (_history.Current.Entries.TryGetValue(key, out var entry)) return entry;
            return null;
        }
    }
}
