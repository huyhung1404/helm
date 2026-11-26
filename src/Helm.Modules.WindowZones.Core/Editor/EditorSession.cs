using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Modules.WindowZones.Model;

namespace Helm.Modules.WindowZones.Editor;

/// <summary>
/// One editing session shared by the per-monitor editor windows: every zone of every connected monitor, in global
/// pixels (<see cref="ZoneEditModel"/>). Save replaces the zones; zones keep their id, number, name and windows.
/// </summary>
public sealed partial class EditorSession : ObservableObject
{
    private readonly WindowZonesData _data;
    private readonly Dictionary<string, ZoneDefinition> _saved;

    public EditorSession(WindowZonesData data, IReadOnlyList<MonitorInfo> monitors, MonitorInfo home)
    {
        _data = data;
        Monitors = monitors;
        Home = home;
        var zones = data.GetZones();
        _saved = zones.ToDictionary(z => z.Id, StringComparer.Ordinal);
        var placed = WindowZonesData.Place(zones, monitors);
        Edit = new ZoneEditModel(monitors.Select(m => m.WorkArea).ToList(), placed.Select(p => new EditZone(p.Id, p.Rect)));
        Edit.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(CanEditZone));
            OnPropertyChanged(nameof(Summary));
            VisualsChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? CloseRequested;

    /// <summary>Raised when zones or the selection change; the editor windows repaint.</summary>
    public event EventHandler? VisualsChanged;

    public IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>The monitor the editor was opened on: it shows the toolbar.</summary>
    public MonitorInfo Home { get; }

    public ZoneEditModel Edit { get; }

    public bool CanEditZone => Edit.Selected >= 0;

    public string Summary => Edit.Zones.Count switch
    {
        0 => "No zones yet",
        1 => "1 zone",
        var n => $"{n} zones",
    };

    public string Hint => Monitors.Count > 1
        ? "Drag on empty space to draw a zone (on any monitor) · drag a zone to move it, its edges to resize · right-click deletes · Esc cancels"
        : "Drag on empty space to draw a zone · drag a zone to move it, its edges to resize · right-click deletes · Esc cancels";

    /// <summary>What a zone shows in the editor: its shortcut number and name, or "New" until it is saved.</summary>
    public (string Number, string Name) LabelFor(EditZone zone)
    {
        if (!_saved.TryGetValue(zone.Id, out var saved)) return ("+", "New zone");
        return (saved.Number is { } n ? n.ToString() : "·", saved.Name.Trim());
    }

    [RelayCommand]
    private void SplitVertical() => Edit.SplitSelected(SplitOrientation.Vertical);

    [RelayCommand]
    private void SplitHorizontal() => Edit.SplitSelected(SplitOrientation.Horizontal);

    [RelayCommand]
    private void DeleteZone() => Edit.DeleteSelected();

    [RelayCommand]
    private void DeleteAll() => Edit.DeleteAll();

    [RelayCommand]
    private void Save()
    {
        var zones = new List<ZoneDefinition>();
        foreach (var zone in Edit.Zones)
        {
            var area = ZoneGeometry.MostOverlapping(Edit.WorkAreas, zone.Rect);
            if (area < 0) continue;
            var monitor = Monitors[area];
            var definition = _saved.TryGetValue(zone.Id, out var saved) ? saved.Clone() : new ZoneDefinition { Id = zone.Id };
            definition.MonitorId = monitor.Id;
            definition.Rect = ZoneGeometry.FromPixels(ZoneGeometry.Intersect(zone.Rect, monitor.WorkArea), monitor.WorkArea);
            zones.Add(definition);
        }
        _data.SaveZones(zones, Monitors);
        Close();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Escape: stop drawing first, otherwise close without saving.</summary>
    public void HandleEscape()
    {
        if (Edit.IsDragging) Edit.Release();
        else Close();
    }

    public void HandleDelete() => Edit.DeleteSelected();

    public static PixelRect? Visible(PixelRect zone, MonitorInfo monitor) =>
        zone.IntersectsWith(monitor.Bounds) ? ZoneGeometry.Intersect(zone, monitor.Bounds) : null;
}
