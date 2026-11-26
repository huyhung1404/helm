using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Core.Services;
using Helm.Core.Settings;

namespace Helm.Modules.AlwaysOnTop;

/// <summary>Always On Top's page: the shortcut, the border around kept windows, the sound, the excluded apps and the windows kept on top now.</summary>
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

    public AlwaysOnTopViewModel(IAlwaysOnTop module, IUiDispatcher ui)
    {
        Module = module;
        _store = module.Settings;
        _ui = ui;
        Load(_store.Current);
        module.PinnedWindowsChanged += (_, _) => _ui.Post(RefreshPinned);
        RefreshPinned();
    }

    public IAlwaysOnTop Module { get; }

    public HotkeyGesture DefaultHotkey => AlwaysOnTopSettings.DefaultHotkey;

    public IReadOnlyList<int> Thicknesses { get; } = [1, 2, 3, 4, 5, 6, 8, 10, 12];

    /// <summary>The border colours to pick from (the Windows accent palette, then white and grey).</summary>
    public IReadOnlyList<string> BorderColors { get; } =
    [
        "#0078D4", "#0099BC", "#00B7C3", "#00CC6A", "#10893E", "#107C10", "#7E735F", "#FFB900",
        "#F7630C", "#E81123", "#EA005E", "#C239B3", "#8764B8", "#744DA9", "#FFFFFF", "#767676",
    ];

    /// <summary>A colour of one's own can be picked only while the border shows and does not follow Windows.</summary>
    public bool CanPickColor => ShowBorder && !UseAccentColor;

    public ObservableCollection<PinnedWindowInfo> Pinned { get; } = [];

    public bool HasPinned => Pinned.Count > 0;

    /// <summary>"2 windows on top", "Nothing on top".</summary>
    public string PinnedSummary => Pinned.Count switch
    {
        0 => "Nothing on top",
        1 => "1 window on top",
        var n => $"{n} windows on top",
    };

    [RelayCommand]
    private void Unpin(PinnedWindowInfo? window)
    {
        if (window is not null) Module.Unpin(window.Handle);
    }

    [RelayCommand]
    private Task UnpinAllAsync() => Module.UnpinAllAsync();

    [RelayCommand]
    private void PickColor(string? color)
    {
        if (!string.IsNullOrWhiteSpace(color)) BorderColor = color;
    }

    partial void OnHotkeyChanged(HotkeyGesture value) => Save(s => s.Hotkey = value);

    partial void OnShowBorderChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPickColor));
        Save(s => s.ShowBorder = value);
    }

    partial void OnUseAccentColorChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPickColor));
        Save(s => s.UseAccentColor = value);
    }

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
        OnPropertyChanged(nameof(PinnedSummary));
    }
}
