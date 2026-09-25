using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Settings;
using Helm.Modules.Zones.Layouts;

namespace Helm.Modules.Zones;

/// <summary>Zones for one monitor, ready for hit testing (physical pixels).</summary>
public sealed record MonitorZones(MonitorInfo Monitor, LayoutDefinition Layout, AppliedLayout Applied, IReadOnlyList<PixelRect> Zones)
{
    public int Sensitivity => (int)Math.Round(Layout.SensitivityRadius * Monitor.Scale);
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

        if (_layouts.Current.EnsureDefaults()) _layouts.Update(_ => { });
    }

    /// <summary>Raised (on the writer's thread) after layouts or applied layouts change.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<LayoutDefinition> GetLayouts()
    {
        lock (_gate) return _layouts.Current.Layouts.Select(l => l.Clone()).ToList();
    }

    public LayoutDefinition? GetLayout(string id)
    {
        lock (_gate) return _layouts.Current.Find(id)?.Clone();
    }

    /// <summary>The layout applied to <paramref name="monitor"/> (a sensible default when none was chosen yet).</summary>
    public (LayoutDefinition Layout, AppliedLayout Applied) GetApplied(MonitorInfo monitor)
    {
        lock (_gate)
        {
            if (_applied.Current.Monitors.TryGetValue(monitor.Id, out var applied) && _layouts.Current.Find(applied.LayoutId) is { } layout)
                return (layout.Clone(), Copy(applied));

            // Portrait screens stack rows; landscape screens get the priority grid, like PowerToys.
            var kind = monitor.WorkArea.Height > monitor.WorkArea.Width ? LayoutKind.Rows : LayoutKind.PriorityGrid;
            var fallback = _layouts.Current.Find(LayoutTemplates.TemplateId(kind)) ?? LayoutTemplates.Defaults().First(l => l.Kind == kind);
            return (fallback.Clone(), new AppliedLayout { LayoutId = fallback.Id, Spacing = fallback.Spacing, ShowSpacing = fallback.ShowSpacing });
        }
    }

    /// <summary>Pixel zones for <paramref name="monitor"/> with spacing applied and scaled for its DPI.</summary>
    public MonitorZones GetZones(MonitorInfo monitor)
    {
        var (layout, applied) = GetApplied(monitor);
        var spacing = applied.ShowSpacing && layout.UsesSpacing ? ZoneMath.ScaleSpacing(applied.Spacing, monitor.Dpi) : 0;
        var zones = ZoneMath.ToPixels(layout.GetZones(), monitor.WorkArea, spacing, spacing > 0);
        return new MonitorZones(monitor, layout, applied, zones);
    }

    public void Apply(string monitorId, LayoutDefinition layout, int spacing, bool showSpacing)
    {
        lock (_gate)
        {
            SaveLayoutLocked(layout);
            _applied.Update(f => f.Monitors[monitorId] = new AppliedLayout { LayoutId = layout.Id, Spacing = spacing, ShowSpacing = showSpacing });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds or replaces a layout (templates keep their id and only store their parameters).</summary>
    public void SaveLayout(LayoutDefinition layout)
    {
        lock (_gate) SaveLayoutLocked(layout);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteLayout(string id)
    {
        lock (_gate)
        {
            var layout = _layouts.Current.Find(id);
            if (layout is null || layout.IsTemplate) return;
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
                // Keep the file small: drop the oldest entries beyond 500.
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

    private void SaveLayoutLocked(LayoutDefinition layout)
    {
        var copy = layout.Clone();
        _layouts.Update(f =>
        {
            var index = f.Layouts.FindIndex(l => l.Id == copy.Id);
            if (index >= 0) f.Layouts[index] = copy;
            else f.Layouts.Add(copy);
        });
    }

    private static AppliedLayout Copy(AppliedLayout a) => new() { LayoutId = a.LayoutId, Spacing = a.Spacing, ShowSpacing = a.ShowSpacing };
}
