using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Core.Services;
using Helm.Core.Settings;

namespace Helm.Modules.AlwaysOnTop;

public sealed partial class AlwaysOnTopViewModel : ObservableObject
{
    private readonly ISettingsStore<AlwaysOnTopSettings> _store;
    private readonly IUiDispatcher _ui;
    private bool _loading;

    [ObservableProperty] private HotkeyGesture _hotkey;
    [ObservableProperty] private bool _showBorder;
    [ObservableProperty] private bool _useAccentColor;
    [ObservableProperty] private string _borderColor = "#0078D4";
    [ObservableProperty] private int _borderThickness;
    [ObservableProperty] private bool _playSound;
    [ObservableProperty] private bool _doNotActivateInGameMode;
    [ObservableProperty] private string _excludedApps = string.Empty;

    public AlwaysOnTopViewModel(AlwaysOnTopModule module, IUiDispatcher ui)
    {
        Module = module;
        _store = module.Settings;
        _ui = ui;
        Load(_store.Current);
        module.PinnedWindowsChanged += (_, _) => _ui.Post(RefreshPinned);
        RefreshPinned();
    }

    public AlwaysOnTopModule Module { get; }

    public HotkeyGesture DefaultHotkey => AlwaysOnTopSettings.DefaultHotkey;

    public IReadOnlyList<int> Thicknesses { get; } = [1, 2, 3, 4, 5, 6, 8, 10, 12];

    public ObservableCollection<PinnedWindowInfo> Pinned { get; } = [];

    public bool HasPinned => Pinned.Count > 0;

    [RelayCommand]
    private void Unpin(PinnedWindowInfo? window)
    {
        if (window is not null) Module.Unpin(window.Handle);
    }

    [RelayCommand]
    private Task UnpinAllAsync() => Module.UnpinAllAsync();

    partial void OnHotkeyChanged(HotkeyGesture value) => Save(s => s.Hotkey = value);
    partial void OnShowBorderChanged(bool value) => Save(s => s.ShowBorder = value);
    partial void OnUseAccentColorChanged(bool value) => Save(s => s.UseAccentColor = value);
    partial void OnBorderColorChanged(string value) => Save(s => s.BorderColor = value);
    partial void OnBorderThicknessChanged(int value) => Save(s => s.BorderThickness = value);
    partial void OnPlaySoundChanged(bool value) => Save(s => s.PlaySound = value);
    partial void OnDoNotActivateInGameModeChanged(bool value) => Save(s => s.DoNotActivateInGameMode = value);
    partial void OnExcludedAppsChanged(string value) => Save(s => s.ExcludedApps = ProcessExclusions.ParseLines(value));

    private void Load(AlwaysOnTopSettings s)
    {
        _loading = true;
        Hotkey = s.Hotkey;
        ShowBorder = s.ShowBorder;
        UseAccentColor = s.UseAccentColor;
        BorderColor = s.BorderColor;
        BorderThickness = s.BorderThickness;
        PlaySound = s.PlaySound;
        DoNotActivateInGameMode = s.DoNotActivateInGameMode;
        ExcludedApps = string.Join(Environment.NewLine, s.ExcludedApps);
        _loading = false;
    }

    private void Save(Action<AlwaysOnTopSettings> apply)
    {
        if (!_loading) _store.Update(apply);
    }

    private void RefreshPinned()
    {
        Pinned.Clear();
        foreach (var p in Module.PinnedWindows) Pinned.Add(p);
        OnPropertyChanged(nameof(HasPinned));
    }
}
