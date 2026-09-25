using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Modules.Zones.Layouts;

namespace Helm.Modules.Zones.Editor;

public sealed partial class LayoutItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private IReadOnlyList<ZoneRect> _zones = [];

    public LayoutItemViewModel(LayoutDefinition definition)
    {
        Definition = definition;
        Refresh();
    }

    public LayoutDefinition Definition { get; private set; }

    public string Name => Definition.Name;

    public bool IsTemplate => Definition.IsTemplate;

    public string Subtitle => Definition.IsTemplate
        ? $"{Definition.GetZones().Count} zones"
        : $"{LayoutTemplates.DisplayName(Definition.Kind)} · {Definition.GetZones().Count} zones";

    public void Update(LayoutDefinition definition)
    {
        Definition = definition;
        Refresh();
    }

    public void Refresh()
    {
        Zones = Definition.GetZones();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Subtitle));
    }
}

/// <summary>Layout picker + editor state for one monitor.</summary>
public sealed partial class EditorViewModel : ObservableObject
{
    private readonly ZonesDataService _data;
    private LayoutDefinition? _editing;
    private bool _loading;

    [ObservableProperty] private LayoutItemViewModel? _selected;
    [ObservableProperty] private int _zoneCount = 3;
    [ObservableProperty] private int _spacing = 16;
    [ObservableProperty] private bool _showSpacing = true;
    [ObservableProperty] private int _sensitivity = 20;
    [ObservableProperty] private IReadOnlyList<ZoneRect> _previewZones = [];

    // Edit mode
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isEditingGrid;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private GridLayout _editGrid = GridLayout.Single();
    [ObservableProperty] private IReadOnlyList<int> _gridSelection = [];
    [ObservableProperty] private IReadOnlyList<ZoneRect> _editCanvas = [];
    [ObservableProperty] private int _canvasSelection = -1;

    public EditorViewModel(MonitorInfo monitor, ZonesDataService data, int pickerColumns)
    {
        Monitor = monitor;
        _data = data;
        PickerColumns = Math.Clamp(pickerColumns, 2, 6);
        Reload();
    }

    public event EventHandler? CloseRequested;

    public MonitorInfo Monitor { get; }

    public int PickerColumns { get; }

    public string Title => $"Layouts · {Monitor.DisplayName}";

    public double AspectRatio => Monitor.WorkArea.Height == 0 ? 16.0 / 9 : Monitor.WorkArea.Width / (double)Monitor.WorkArea.Height;

    public Size PixelSize => new(Monitor.WorkArea.Width, Monitor.WorkArea.Height);

    public ObservableCollection<LayoutItemViewModel> Templates { get; } = [];

    public ObservableCollection<LayoutItemViewModel> Customs { get; } = [];

    public bool IsSelectedTemplate => Selected?.IsTemplate == true;

    public bool IsSelectedCustom => Selected is { IsTemplate: false };

    public bool SelectedUsesSpacing => Selected?.Definition.UsesSpacing == true;

    public bool CanMerge => GridSelection.Count > 1;

    public bool ShowGridEditor => IsEditing && IsEditingGrid;

    public bool ShowCanvasEditor => IsEditing && !IsEditingGrid;

    public System.Windows.Media.Color Accent { get; } = ToColor(AccentColor.Current);

    public System.Windows.Media.Brush AccentBrush { get; } = new System.Windows.Media.SolidColorBrush(ToColor(AccentColor.Current));

    partial void OnIsEditingChanged(bool value) => NotifyEditorVisibility();

    partial void OnIsEditingGridChanged(bool value) => NotifyEditorVisibility();

    private void NotifyEditorVisibility()
    {
        OnPropertyChanged(nameof(ShowGridEditor));
        OnPropertyChanged(nameof(ShowCanvasEditor));
        OnPropertyChanged(nameof(EditHint));
    }

    private static System.Windows.Media.Color ToColor(Helm.Core.Geometry.RgbColor c) => System.Windows.Media.Color.FromRgb(c.R, c.G, c.B);

    public string EditHint => IsEditingGrid
        ? "Click to split (hold Shift for a horizontal line) · drag a line to resize · drag across zones, then Merge · Esc cancels"
        : "Drag on empty space to add a zone · drag to move · drag edges to resize · Delete or right-click removes · Esc cancels";

    /// <summary>Re-reads layouts and the applied layout for this monitor from disk state.</summary>
    public void Reload()
    {
        var selectedId = Selected?.Definition.Id;
        var (applied, appliedInfo) = _data.GetApplied(Monitor);
        Templates.Clear();
        Customs.Clear();
        foreach (var layout in _data.GetLayouts())
        {
            var item = new LayoutItemViewModel(layout) { IsApplied = layout.Id == applied.Id };
            (layout.IsTemplate ? Templates : Customs).Add(item);
        }

        var toSelect = All().FirstOrDefault(i => i.Definition.Id == (selectedId ?? applied.Id)) ?? All().FirstOrDefault();
        _loading = true;
        Spacing = appliedInfo.Spacing;
        ShowSpacing = appliedInfo.ShowSpacing;
        _loading = false;
        Select(toSelect);
    }

    [RelayCommand]
    private void Select(LayoutItemViewModel? item)
    {
        if (item is null) return;
        foreach (var i in All()) i.IsSelected = i == item;
        Selected = item;
        _loading = true;
        ZoneCount = item.Definition.ZoneCount;
        Sensitivity = item.Definition.SensitivityRadius;
        if (!item.IsApplied)
        {
            Spacing = item.Definition.Spacing;
            ShowSpacing = item.Definition.ShowSpacing;
        }
        _loading = false;
        PreviewZones = item.Zones;
        OnPropertyChanged(nameof(IsSelectedTemplate));
        OnPropertyChanged(nameof(IsSelectedCustom));
        OnPropertyChanged(nameof(SelectedUsesSpacing));
    }

    [RelayCommand]
    private void Apply()
    {
        if (Selected is null) return;
        var def = Selected.Definition.Clone();
        def.Spacing = Spacing;
        def.ShowSpacing = ShowSpacing;
        _data.Apply(Monitor.Id, def, Spacing, ShowSpacing);
        foreach (var i in All()) i.IsApplied = i == Selected;
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
    private void NewGrid() => BeginEdit(new LayoutDefinition
    {
        Name = $"Custom grid {Customs.Count + 1}",
        Kind = LayoutKind.CustomGrid,
        Grid = LayoutTemplates.GenerateGrid(LayoutKind.Columns, 2),
    });

    [RelayCommand]
    private void NewCanvas() => BeginEdit(new LayoutDefinition
    {
        Name = $"Custom canvas {Customs.Count + 1}",
        Kind = LayoutKind.CustomCanvas,
        Spacing = 0,
        Canvas = [new ZoneRect(0.1, 0.1, 0.5, 0.8), new ZoneRect(0.55, 0.3, 0.4, 0.5)],
    });

    /// <summary>Edits a custom layout; for templates, edits a new custom copy (grid templates stay grids).</summary>
    [RelayCommand]
    private void Edit()
    {
        if (Selected is null) return;
        var source = Selected.Definition;
        if (!source.IsTemplate)
        {
            BeginEdit(source.Clone());
            return;
        }
        Duplicate();
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is null) return;
        var source = Selected.Definition;
        var copy = new LayoutDefinition
        {
            Name = $"{source.Name} copy",
            Spacing = Spacing,
            ShowSpacing = ShowSpacing,
            SensitivityRadius = Sensitivity,
        };
        if (source.AsGrid() is { } grid)
        {
            copy.Kind = LayoutKind.CustomGrid;
            copy.Grid = grid.Clone();
        }
        else
        {
            copy.Kind = LayoutKind.CustomCanvas;
            copy.Canvas = source.GetZones().ToList();
        }
        BeginEdit(copy);
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { IsTemplate: false } item) return;
        _data.DeleteLayout(item.Definition.Id);
        Selected = null;
        Reload();
    }

    [RelayCommand]
    private void Merge()
    {
        if (GridSelection.Count < 2) return;
        EditGrid = EditGrid.Merge(GridSelection);
        GridSelection = [];
    }

    [RelayCommand]
    private void ResetGrid()
    {
        EditGrid = GridLayout.Single();
        GridSelection = [];
    }

    [RelayCommand]
    private void AddCanvasZone()
    {
        var offset = 0.04 * (EditCanvas.Count % 8);
        EditCanvas = [.. EditCanvas, new ZoneRect(0.3 + offset, 0.25 + offset, 0.4, 0.4).Clamp()];
        CanvasSelection = EditCanvas.Count - 1;
    }

    [RelayCommand]
    private void DeleteCanvasZone()
    {
        if (CanvasSelection < 0 || CanvasSelection >= EditCanvas.Count) return;
        var list = EditCanvas.ToList();
        list.RemoveAt(CanvasSelection);
        EditCanvas = list;
        CanvasSelection = -1;
    }

    [RelayCommand]
    private void SaveEdit()
    {
        if (_editing is null) return;
        _editing.Name = string.IsNullOrWhiteSpace(EditName) ? _editing.Name : EditName.Trim();
        if (_editing.Kind == LayoutKind.CustomGrid) _editing.Grid = EditGrid.Normalize();
        else _editing.Canvas = EditCanvas.ToList();
        _data.SaveLayout(_editing);
        var id = _editing.Id;
        EndEdit();
        Reload();
        Select(All().FirstOrDefault(i => i.Definition.Id == id));
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EndEdit();
        if (Selected is not null) PreviewZones = Selected.Zones;
    }

    /// <summary>Escape: leave edit mode first, otherwise close the editor.</summary>
    public void HandleEscape()
    {
        if (IsEditing) CancelEdit();
        else Close();
    }

    partial void OnZoneCountChanged(int value)
    {
        if (_loading || Selected is not { IsTemplate: true } item) return;
        var def = item.Definition.Clone();
        def.ZoneCount = Math.Clamp(value, 1, LayoutTemplates.MaxZones);
        _data.SaveLayout(def);
        item.Update(def);
        PreviewZones = item.Zones;
        if (item.IsApplied) Apply();
    }

    partial void OnSensitivityChanged(int value)
    {
        if (_loading || Selected is null) return;
        var def = Selected.Definition.Clone();
        def.SensitivityRadius = Math.Clamp(value, 0, 200);
        _data.SaveLayout(def);
        Selected.Update(def);
    }

    partial void OnSpacingChanged(int value)
    {
        if (!_loading && Selected?.IsApplied == true) Apply();
    }

    partial void OnShowSpacingChanged(bool value)
    {
        if (!_loading && Selected?.IsApplied == true) Apply();
    }

    partial void OnGridSelectionChanged(IReadOnlyList<int> value) => OnPropertyChanged(nameof(CanMerge));

    partial void OnEditGridChanged(GridLayout value)
    {
        if (IsEditing && IsEditingGrid) PreviewZones = value.ToZones();
    }

    partial void OnEditCanvasChanged(IReadOnlyList<ZoneRect> value)
    {
        if (IsEditing && !IsEditingGrid) PreviewZones = value;
    }

    private void BeginEdit(LayoutDefinition layout)
    {
        _editing = layout;
        EditName = layout.Name;
        IsEditingGrid = layout.Kind == LayoutKind.CustomGrid;
        EditGrid = layout.Grid ?? GridLayout.Single();
        EditCanvas = layout.Canvas?.ToList() ?? [];
        GridSelection = [];
        CanvasSelection = -1;
        IsEditing = true;
        OnPropertyChanged(nameof(EditHint));
    }

    private void EndEdit()
    {
        _editing = null;
        IsEditing = false;
        GridSelection = [];
    }

    private IEnumerable<LayoutItemViewModel> All() => Templates.Concat(Customs);
}
