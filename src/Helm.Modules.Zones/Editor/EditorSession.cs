using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Modules.Zones.Layouts;

namespace Helm.Modules.Zones.Editor;

public sealed partial class LayoutCardViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isApplied;

    public LayoutCardViewModel(ZoneLayout layout, double aspectRatio)
    {
        Layout = layout;
        AspectRatio = aspectRatio;
    }

    public ZoneLayout Layout { get; }
    public double AspectRatio { get; }
    public string Name => Layout.Name;
    public IReadOnlyList<ZoneRect> Zones => Layout.Zones;
    public string Subtitle => $"{Layout.ScopeText} · {Layout.Zones.Count} zones";
    public string NumberText => Layout.Number is { } n ? n.ToString() : string.Empty;
    public bool HasNumber => Layout.Number is not null;
}

/// <summary>
/// One editor session shared by the per-monitor editor windows: the layout list (picker) and, while editing, a
/// <see cref="ZoneEditModel"/> in global pixels so zones can be drawn across monitors.
/// </summary>
public sealed partial class EditorSession : ObservableObject
{
    private readonly ZonesDataService _data;
    private ZoneLayout? _editing;

    [ObservableProperty] private LayoutCardViewModel? _selected;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private int _editNumberIndex;

    public EditorSession(ZonesDataService data, IReadOnlyList<MonitorInfo> monitors, MonitorInfo home)
    {
        _data = data;
        Monitors = monitors;
        Home = home;
        Reload();
    }

    public event EventHandler? CloseRequested;

    /// <summary>Raised when zones, the selection or the mode change; editor windows repaint.</summary>
    public event EventHandler? VisualsChanged;

    public IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>The monitor the editor was opened on: it shows the picker/toolbar; Apply targets it.</summary>
    public MonitorInfo Home { get; }

    public ObservableCollection<LayoutCardViewModel> Layouts { get; } = [];

    public ZoneEditModel? Edit { get; private set; }

    public string Title => $"Zone layouts · {Home.DisplayName}";

    public IReadOnlyList<string> NumberOptions { get; } = ["No shortcut", .. Enumerable.Range(1, ZoneLayout.MaxNumber).Select(n => $"Ctrl+Win+Alt+{n}")];

    public bool HasSelection => Selected is not null;

    public bool CanEditZone => Edit?.Selected >= 0;

    public string EditHint => "Drag on empty space to draw a zone (it can cross monitors) · drag a zone to move it · drag its edges to resize · edges snap to zones and monitors · Delete removes the selected zone · Esc cancels";

    public bool IsMultiMonitor => Monitors.Count > 1;

    /// <summary>Global pixel zones to show on <paramref name="monitor"/> and whether they are editable there.</summary>
    public (IReadOnlyList<PixelRect> Zones, int Selected, PixelRect? Drawing, PixelRect? Reference) VisualsFor(MonitorInfo monitor)
    {
        if (Edit is { } edit)
            return (edit.Zones, edit.Selected, edit.Drawing, edit.Reference);

        if (Selected?.Layout is not { } layout) return ([], -1, null, null);
        var covered = LayoutGeometry.CoveredMonitors(layout, Home, Monitors);
        if (!covered.Any(m => m.Id == monitor.Id)) return ([], -1, null, null);
        return LayoutGeometry.ReferenceRect(layout, Home, Monitors) is { } reference
            ? (LayoutGeometry.ToPixels(layout.Zones, reference), -1, null, reference)
            : ([], -1, null, null);
    }

    public void Reload()
    {
        var selectedId = Selected?.Layout.Id;
        var applied = _data.GetAppliedLayout(Home)?.Id;
        Layouts.Clear();
        foreach (var layout in _data.GetLayouts().OrderBy(l => l.Number ?? int.MaxValue).ThenBy(l => l.Name))
        {
            var reference = LayoutGeometry.ReferenceRect(layout, Home, Monitors);
            var aspect = reference is { Height: > 0 } r ? r.Width / (double)r.Height : 16.0 / 9;
            Layouts.Add(new LayoutCardViewModel(layout, aspect) { IsApplied = layout.Id == applied });
        }
        Select(Layouts.FirstOrDefault(l => l.Layout.Id == (selectedId ?? applied)) ?? Layouts.FirstOrDefault());
    }

    [RelayCommand]
    private void Select(LayoutCardViewModel? card)
    {
        foreach (var l in Layouts) l.IsSelected = l == card;
        Selected = card;
        OnPropertyChanged(nameof(HasSelection));
        RaiseVisuals();
    }

    [RelayCommand]
    private void Apply()
    {
        if (Selected is null) return;
        _data.Apply(Selected.Layout, Home, Monitors);
        foreach (var l in Layouts) l.IsApplied = l == Selected;
    }

    [RelayCommand]
    private void ApplyAndClose()
    {
        Apply();
        Close();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NewForMonitor()
    {
        var layout = new ZoneLayout { Name = NextName(), Scope = LayoutScope.Monitor, MonitorIds = [Home.Id], Number = FreeNumber() };
        var halves = LayoutGeometry.Split(Home.WorkArea, SplitOrientation.Vertical);
        BeginEdit(layout, Home.WorkArea, [halves.First, halves.Second]);
    }

    [RelayCommand]
    private void NewAcrossMonitors()
    {
        var layout = new ZoneLayout { Name = NextName(), Scope = LayoutScope.Span, MonitorIds = Monitors.Select(m => m.Id).ToList(), Number = FreeNumber() };
        var reference = Monitors.Select(m => m.WorkArea).Aggregate((a, b) => a.Union(b));
        BeginEdit(layout, reference, Monitors.Select(m => m.WorkArea));
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected?.Layout is not { } layout) return;
        if (LayoutGeometry.ReferenceRect(layout, Home, Monitors) is not { } reference) return;
        BeginEdit(layout.Clone(), reference, LayoutGeometry.ToPixels(layout.Zones, reference));
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected?.Layout is not { } source) return;
        if (LayoutGeometry.ReferenceRect(source, Home, Monitors) is not { } reference) return;
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = $"{source.Name} copy";
        copy.Number = FreeNumber();
        BeginEdit(copy, reference, LayoutGeometry.ToPixels(source.Zones, reference));
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected?.Layout is not { } layout) return;
        _data.DeleteLayout(layout.Id);
        Selected = null;
        Reload();
    }

    [RelayCommand]
    private void SplitVertical() => Edit?.SplitSelected(SplitOrientation.Vertical);

    [RelayCommand]
    private void SplitHorizontal() => Edit?.SplitSelected(SplitOrientation.Horizontal);

    [RelayCommand]
    private void DeleteZone() => Edit?.DeleteSelected();

    [RelayCommand]
    private void ResetZones() => Edit?.ResetToWorkAreas();

    [RelayCommand]
    private void SaveEdit()
    {
        if (_editing is null || Edit is null) return;
        _editing.Name = string.IsNullOrWhiteSpace(EditName) ? _editing.Name : EditName.Trim();
        _editing.Number = EditNumberIndex <= 0 ? null : EditNumberIndex;
        _editing.Zones = Edit.ToLayoutZones();
        _data.SaveLayout(_editing);
        var id = _editing.Id;
        EndEdit();
        Reload();
        Select(Layouts.FirstOrDefault(l => l.Layout.Id == id));
    }

    [RelayCommand]
    private void CancelEdit() => EndEdit();

    /// <summary>Escape: leave edit mode first, otherwise close the editor.</summary>
    public void HandleEscape()
    {
        if (IsEditing) CancelEdit();
        else Close();
    }

    public void HandleDelete()
    {
        if (IsEditing) Edit?.DeleteSelected();
    }

    private void BeginEdit(ZoneLayout layout, PixelRect reference, IEnumerable<PixelRect> zones)
    {
        _editing = layout;
        EditName = layout.Name;
        EditNumberIndex = layout.Number ?? 0;
        var areas = LayoutGeometry.CoveredMonitors(layout, Home, Monitors).Select(m => m.WorkArea).ToList();
        Edit = new ZoneEditModel(reference, areas, zones);
        Edit.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(CanEditZone));
            RaiseVisuals();
        };
        IsEditing = true;
        OnPropertyChanged(nameof(Edit));
        RaiseVisuals();
    }

    private void EndEdit()
    {
        _editing = null;
        Edit = null;
        IsEditing = false;
        OnPropertyChanged(nameof(Edit));
        OnPropertyChanged(nameof(CanEditZone));
        RaiseVisuals();
    }

    private string NextName()
    {
        var n = Layouts.Count + 1;
        while (Layouts.Any(l => l.Name == $"Layout {n}")) n++;
        return $"Layout {n}";
    }

    private int? FreeNumber()
    {
        var used = Layouts.Select(l => l.Layout.Number).OfType<int>().ToHashSet();
        for (var n = 1; n <= ZoneLayout.MaxNumber; n++)
            if (!used.Contains(n)) return n;
        return null;
    }

    private void RaiseVisuals() => VisualsChanged?.Invoke(this, EventArgs.Empty);
}
