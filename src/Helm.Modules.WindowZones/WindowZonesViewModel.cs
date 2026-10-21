using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.WindowZones.Engine;
using Helm.Modules.WindowZones.Model;

namespace Helm.Modules.WindowZones;

/// <summary>A window in a zone card: its icon, title and program, with Show and Take out.</summary>
public sealed partial class ZoneWindowRowViewModel(WindowZonesModule module, ZoneWindowSnapshot window) : ObservableObject
{
    public string Title => string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;

    public string Detail => window.IsMinimized ? $"{window.ProcessName} · minimized" : window.IsTop ? $"{window.ProcessName} · on top" : window.ProcessName;

    public ImageSource? Icon => string.IsNullOrEmpty(window.ProcessPath) ? null : ShellIcons.ForFile(window.ProcessPath);

    public bool IsTop => window.IsTop;

    [RelayCommand]
    private void Show() => module.ShowWindow(window.Handle);

    [RelayCommand]
    private void Release() => module.Release(window.Handle);
}

/// <summary>One zone on the content page: its shortcut, name, state and windows.</summary>
public sealed partial class ZoneCardViewModel : ObservableObject
{
    private readonly WindowZonesViewModel _owner;
    private bool _loading;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private int _numberIndex;
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _monitorName = string.Empty;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isClosed;
    [ObservableProperty] private bool _hasWindows;
    [ObservableProperty] private IReadOnlyList<string> _shortcutCaps = [];
    [ObservableProperty] private IReadOnlyList<ZoneWindowRowViewModel> _windows = [];

    public ZoneCardViewModel(WindowZonesViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
    }

    public string Id { get; }

    public IReadOnlyList<string> NumberOptions => _owner.NumberOptions;

    public bool CanOpen => IsConnected && HasWindows;

    public void Update(ZoneSnapshot zone, HotkeyModifiers modifiers, bool toolOn)
    {
        _loading = true;
        if (Name != zone.Name) Name = zone.Name;
        NumberIndex = zone.Number ?? 0;
        Label = zone.Label;
        MonitorName = zone.MonitorName;
        IsConnected = zone.IsConnected;
        IsClosed = zone.IsClosed;
        HasWindows = zone.Windows.Count > 0;
        ShortcutCaps = zone.Number is { } n ? new HotkeyGesture(modifiers, '0' + n).ToKeycaps() : [];
        StateText = !zone.IsConnected ? "Its monitor is not connected"
            : !toolOn ? "Turn Window Zones on to use it"
            : zone.Windows.Count == 0 ? $"Empty · hold {_owner.DragKeyName} while dragging a window here"
            : $"{(zone.IsClosed ? "Closed" : "Open")} · {(zone.Windows.Count == 1 ? "1 window" : $"{zone.Windows.Count} windows")}";
        Windows = zone.Windows.Select(w => new ZoneWindowRowViewModel(_owner.Module, w)).ToList();
        OnPropertyChanged(nameof(CanOpen));
        _loading = false;
    }

    /// <summary>Saved when the box loses focus (see the page), not on every key.</summary>
    partial void OnNameChanged(string value)
    {
        if (!_loading) _owner.Module.Data.Rename(Id, value);
    }

    partial void OnNumberIndexChanged(int value)
    {
        if (!_loading) _owner.Module.Data.SetNumber(Id, value <= 0 ? null : value);
    }

    [RelayCommand]
    private void Open() => _owner.Module.OpenZone(Id);

    [RelayCommand]
    private void Close() => _owner.Module.CloseZone(Id);

    [RelayCommand]
    private void ShowWhere() => _owner.Module.ShowZone(Id);
}

/// <summary>Both pages of Window Zones: the zones and their windows (content) and the options (settings).</summary>
public sealed partial class WindowZonesViewModel : ObservableObject
{
    private readonly ISettingsStore<WindowZonesSettings> _store;
    private readonly IUiDispatcher _ui;
    private bool _loading;

    [ObservableProperty] private int _zoneModifiersIndex;
    [ObservableProperty] private HotkeyGesture _nextWindowHotkey;
    [ObservableProperty] private HotkeyGesture _previousWindowHotkey;
    [ObservableProperty] private HotkeyGesture _editorHotkey;
    [ObservableProperty] private int _dragKeyIndex;
    [ObservableProperty] private bool _restoreSizeOnLeave;
    [ObservableProperty] private bool _showTabStrip;
    [ObservableProperty] private double _zoneOpacity;
    [ObservableProperty] private bool _showZoneNumbers;
    [ObservableProperty] private string _excludedAppsText = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _hasZones;

    public WindowZonesViewModel(WindowZonesModule module, IUiDispatcher ui)
    {
        Module = module;
        _ui = ui;
        _store = module.Settings;
        Load(_store.Current);
        _store.Changed += (_, _) => _ui.Post(() =>
        {
            Load(_store.Current);
            RefreshZones();
        });
        module.ZonesChanged += (_, _) => _ui.Post(RefreshZones);
        module.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Core.Modules.IModule.IsEnabled)) _ui.Post(RefreshZones);
        };
        RefreshZones();
    }

    public WindowZonesModule Module { get; }

    public ObservableCollection<ZoneCardViewModel> Zones { get; } = [];

    public HotkeyGesture DefaultNextWindowHotkey => WindowZonesSettings.DefaultNextWindow;
    public HotkeyGesture DefaultPreviousWindowHotkey => WindowZonesSettings.DefaultPreviousWindow;
    public HotkeyGesture DefaultEditorHotkey => WindowZonesSettings.DefaultEditorHotkey;

    public IReadOnlyList<string> ZoneModifierNames { get; } = WindowZonesSettings.ZoneModifierChoices
        .Select(m => $"{string.Join("+", new HotkeyGesture(m, 0).ToKeycaps())}+1…9").ToList();

    public IReadOnlyList<string> DragKeyNames { get; } = Enum.GetNames<ZoneDragKey>();

    public string DragKeyName => _store.Current.DragKey.ToString();

    /// <summary>"No shortcut", then the numbers 1…9 (the zone's keycaps next to it show the whole shortcut).</summary>
    public IReadOnlyList<string> NumberOptions { get; } =
        ["No shortcut", .. Enumerable.Range(1, ZoneDefinition.MaxNumber).Select(n => $"Shortcut {n}")];

    public int MinOpacity => WindowZonesSettings.MinOpacity;
    public int MaxOpacity => WindowZonesSettings.MaxOpacity;
    public string ZoneOpacityText => $"{Math.Round(ZoneOpacity)} %";

    [RelayCommand]
    private void OpenEditor() => Module.OpenEditor();

    partial void OnZoneModifiersIndexChanged(int value)
    {
        if (value < 0 || value >= WindowZonesSettings.ZoneModifierChoices.Count) return;
        Save(s => s.ZoneModifiers = WindowZonesSettings.ZoneModifierChoices[value]);
    }

    partial void OnNextWindowHotkeyChanged(HotkeyGesture value) => Save(s => s.NextWindowHotkey = value);
    partial void OnPreviousWindowHotkeyChanged(HotkeyGesture value) => Save(s => s.PreviousWindowHotkey = value);
    partial void OnEditorHotkeyChanged(HotkeyGesture value) => Save(s => s.EditorHotkey = value);

    partial void OnDragKeyIndexChanged(int value)
    {
        if (value >= 0 && value < DragKeyNames.Count) Save(s => s.DragKey = (ZoneDragKey)value);
    }

    partial void OnRestoreSizeOnLeaveChanged(bool value) => Save(s => s.RestoreSizeOnLeave = value);
    partial void OnShowTabStripChanged(bool value) => Save(s => s.ShowTabStrip = value);

    partial void OnZoneOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(ZoneOpacityText));
        Save(s => s.ZoneOpacity = (int)Math.Round(value));
    }

    partial void OnShowZoneNumbersChanged(bool value) => Save(s => s.ShowZoneNumbers = value);
    partial void OnExcludedAppsTextChanged(string value) => Save(s => s.ExcludedApps = ProcessExclusions.ParseLines(value));

    private void Load(WindowZonesSettings s)
    {
        _loading = true;
        ZoneModifiersIndex = Math.Max(0, WindowZonesSettings.ZoneModifierChoices.ToList().IndexOf(s.ZoneModifiers));
        NextWindowHotkey = s.NextWindowHotkey;
        PreviousWindowHotkey = s.PreviousWindowHotkey;
        EditorHotkey = s.EditorHotkey;
        DragKeyIndex = (int)s.DragKey;
        RestoreSizeOnLeave = s.RestoreSizeOnLeave;
        ShowTabStrip = s.ShowTabStrip;
        ZoneOpacity = s.ZoneOpacity;
        ShowZoneNumbers = s.ShowZoneNumbers;
        if (ProcessExclusions.ParseLines(ExcludedAppsText) is var current && !current.SequenceEqual(s.ExcludedApps))
            ExcludedAppsText = string.Join(Environment.NewLine, s.ExcludedApps);
        _loading = false;
        OnPropertyChanged(nameof(DragKeyName));
    }

    private void Save(Action<WindowZonesSettings> apply)
    {
        if (!_loading) _store.Update(apply);
    }

    /// <summary>Updates the cards in place (so a name being typed keeps its box), adding and removing as zones come and go.</summary>
    private void RefreshZones()
    {
        var snapshot = Module.Zones;
        var on = Module.IsEnabled;
        var modifiers = _store.Current.ZoneModifiers;
        for (var i = Zones.Count - 1; i >= 0; i--)
            if (snapshot.All(z => z.Id != Zones[i].Id)) Zones.RemoveAt(i);
        for (var i = 0; i < snapshot.Count; i++)
        {
            var zone = snapshot[i];
            var card = Zones.FirstOrDefault(c => c.Id == zone.Id);
            if (card is null)
            {
                card = new ZoneCardViewModel(this, zone.Id);
                Zones.Insert(Math.Min(i, Zones.Count), card);
            }
            else if (Zones.IndexOf(card) != i) Zones.Move(Zones.IndexOf(card), Math.Min(i, Zones.Count - 1));
            card.Update(zone, modifiers, on);
        }

        HasZones = snapshot.Count > 0;
        var windows = snapshot.Sum(z => z.Windows.Count);
        var monitors = snapshot.Where(z => z.IsConnected).Select(z => z.MonitorName).Distinct().Count();
        Summary = snapshot.Count == 0
            ? $"No zones yet. Draw them on your screens, then hold {DragKeyName} while dragging a window to drop it in one."
            : $"{Count(snapshot.Count, "zone")} on {Count(monitors, "monitor")} · {Count(windows, "window")} in them";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
