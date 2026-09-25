using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.Zones;

public sealed partial class ZonesViewModel : ObservableObject
{
    private readonly ISettingsStore<ZonesSettings> _store;
    private bool _loading;

    [ObservableProperty] private ZoneActivationKey _activationKey;
    [ObservableProperty] private bool _alwaysShowZones;
    [ObservableProperty] private bool _multiZoneSpanning;
    [ObservableProperty] private HotkeyGesture _editorHotkey;
    [ObservableProperty] private bool _overrideWindowsSnap;
    [ObservableProperty] private bool _restoreSizeOnUnsnap;
    [ObservableProperty] private bool _moveNewWindowsToLastZone;
    [ObservableProperty] private double _zoneOpacity;
    [ObservableProperty] private string _zoneColor = "#2B2B2B";
    [ObservableProperty] private string _borderColor = "#FFFFFF";
    [ObservableProperty] private string _highlightColor = "#0078D4";
    [ObservableProperty] private bool _showZoneNumbers;
    [ObservableProperty] private int _pickerColumns;
    [ObservableProperty] private string _excludedApps = string.Empty;

    public ZonesViewModel(ZonesModule module)
    {
        Module = module;
        _store = module.Settings;
        Load(_store.Current);
    }

    public ZonesModule Module { get; }

    public HotkeyGesture DefaultEditorHotkey => ZonesSettings.DefaultEditorHotkey;

    public IReadOnlyList<ZoneActivationKey> ActivationKeys { get; } = Enum.GetValues<ZoneActivationKey>();

    public IReadOnlyList<int> PickerColumnOptions { get; } = [2, 3, 4, 5, 6];

    public string SpanKeyText => ActivationKey == ZoneActivationKey.Ctrl ? "Alt" : "Ctrl";

    [RelayCommand]
    private void OpenEditor() => Module.OpenEditor();

    partial void OnActivationKeyChanged(ZoneActivationKey value)
    {
        Save(s => s.ActivationKey = value);
        OnPropertyChanged(nameof(SpanKeyText));
    }

    partial void OnAlwaysShowZonesChanged(bool value) => Save(s => s.AlwaysShowZones = value);
    partial void OnMultiZoneSpanningChanged(bool value) => Save(s => s.MultiZoneSpanning = value);
    partial void OnEditorHotkeyChanged(HotkeyGesture value) => Save(s => s.EditorHotkey = value);
    partial void OnOverrideWindowsSnapChanged(bool value) => Save(s => s.OverrideWindowsSnap = value);
    partial void OnRestoreSizeOnUnsnapChanged(bool value) => Save(s => s.RestoreSizeOnUnsnap = value);
    partial void OnMoveNewWindowsToLastZoneChanged(bool value) => Save(s => s.MoveNewWindowsToLastZone = value);
    partial void OnZoneOpacityChanged(double value) => Save(s => s.ZoneOpacity = (int)Math.Round(value));
    partial void OnZoneColorChanged(string value) => Save(s => s.ZoneColor = value);
    partial void OnBorderColorChanged(string value) => Save(s => s.BorderColor = value);
    partial void OnHighlightColorChanged(string value) => Save(s => s.HighlightColor = value);
    partial void OnShowZoneNumbersChanged(bool value) => Save(s => s.ShowZoneNumbers = value);
    partial void OnPickerColumnsChanged(int value) => Save(s => s.PickerColumns = value);
    partial void OnExcludedAppsChanged(string value) => Save(s => s.ExcludedApps = ProcessExclusions.ParseLines(value));

    private void Load(ZonesSettings s)
    {
        _loading = true;
        ActivationKey = s.ActivationKey;
        AlwaysShowZones = s.AlwaysShowZones;
        MultiZoneSpanning = s.MultiZoneSpanning;
        EditorHotkey = s.EditorHotkey;
        OverrideWindowsSnap = s.OverrideWindowsSnap;
        RestoreSizeOnUnsnap = s.RestoreSizeOnUnsnap;
        MoveNewWindowsToLastZone = s.MoveNewWindowsToLastZone;
        ZoneOpacity = s.ZoneOpacity;
        ZoneColor = s.ZoneColor;
        BorderColor = s.BorderColor;
        HighlightColor = s.HighlightColor;
        ShowZoneNumbers = s.ShowZoneNumbers;
        PickerColumns = s.PickerColumns;
        ExcludedApps = string.Join(Environment.NewLine, s.ExcludedApps);
        _loading = false;
    }

    private void Save(Action<ZonesSettings> apply)
    {
        if (!_loading) _store.Update(apply);
    }
}
