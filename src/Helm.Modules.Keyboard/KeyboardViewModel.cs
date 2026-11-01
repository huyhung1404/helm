using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Core.Services;
using Helm.Core.Settings;

namespace Helm.Modules.Keyboard;

public sealed record Choice<T>(T Value, string Label);

/// <summary>An app with a keyboard of its own, in the list on the settings page.</summary>
public sealed partial class KeyboardAppRow : ObservableObject
{
    private readonly KeyboardApps _apps;

    [ObservableProperty] private KeyboardMode _mode;

    public KeyboardAppRow(KeyboardApps apps, KeyboardAppMode app)
    {
        _apps = apps;
        App = app.App;
        _mode = app.Mode;
    }

    public string App { get; }

    partial void OnModeChanged(KeyboardMode value) => _apps.Remember(App, value);
}

public sealed partial class KeyboardViewModel : ObservableObject
{
    private readonly ISettingsStore<KeyboardSettings> _store;
    private readonly IInputMethods _inputMethods;
    private readonly IUiDispatcher _ui;
    private bool _loading;

    [ObservableProperty] private SwitchShortcut _shortcut;
    [ObservableProperty] private HotkeyGesture _hotkey;
    [ObservableProperty] private bool _includeChinese;
    [ObservableProperty] private bool _showIndicator;
    [ObservableProperty] private bool _playSound;
    [ObservableProperty] private bool _modernToneStyle;
    [ObservableProperty] private bool _spellCheck;
    [ObservableProperty] private string _excludedApps = string.Empty;

    [ObservableProperty] private string _installDescription = string.Empty;
    [ObservableProperty] private string _installButton = "Install";
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand))]
    private bool _isBusy;

    public KeyboardViewModel(KeyboardModule module, IInputMethods inputMethods, IUiDispatcher ui)
    {
        Module = module;
        _store = module.Settings;
        _inputMethods = inputMethods;
        _ui = ui;
        Load(_store.Current);
        module.InstallChanged += (_, _) => _ui.Post(RefreshInstall);
        module.Apps.Changed += (_, _) => _ui.Post(RefreshApps);
        RefreshInstall();
        RefreshApps();
    }

    public KeyboardModule Module { get; }

    public HotkeyGesture DefaultHotkey => KeyboardSettings.DefaultHotkey;

    public IReadOnlyList<Choice<SwitchShortcut>> Shortcuts { get; } =
    [
        new(SwitchShortcut.LeftCtrlShift, "Left Ctrl+Shift (press and let go)"),
        new(SwitchShortcut.RightCtrl, "Right Ctrl (press and let go)"),
        new(SwitchShortcut.Hotkey, "A shortcut of your own"),
    ];

    public IReadOnlyList<Choice<KeyboardMode>> Modes { get; } =
    [
        new(KeyboardMode.English, "English"),
        new(KeyboardMode.Vietnamese, "Vietnamese"),
        new(KeyboardMode.Chinese, "Chinese"),
    ];

    public bool IsOwnHotkey => Shortcut == SwitchShortcut.Hotkey;

    /// <summary>A Chinese keyboard (Sogou, Microsoft Pinyin) is in Windows' keyboard list.</summary>
    public bool HasChinese => _inputMethods.HasLanguage(KeyboardModes.ChineseSimplified);

    public ObservableCollection<KeyboardAppRow> Apps { get; } = [];

    public bool HasApps => Apps.Count > 0;

    [RelayCommand(CanExecute = nameof(CanChangeInstall))]
    private async Task InstallAsync()
    {
        IsBusy = true;
        try { await Module.InstallAsync(); }
        finally { IsBusy = false; RefreshInstall(); }
    }

    [RelayCommand(CanExecute = nameof(CanChangeInstall))]
    private async Task UninstallAsync()
    {
        IsBusy = true;
        try { await Module.UninstallAsync(); }
        finally { IsBusy = false; RefreshInstall(); }
    }

    private bool CanChangeInstall() => !IsBusy;

    [RelayCommand]
    private void Forget(KeyboardAppRow? row)
    {
        if (row is not null) Module.Apps.Forget(row.App);
    }

    partial void OnShortcutChanged(SwitchShortcut value)
    {
        OnPropertyChanged(nameof(IsOwnHotkey));
        Save(s => s.Shortcut = value);
    }

    partial void OnHotkeyChanged(HotkeyGesture value) => Save(s => s.Hotkey = value);
    partial void OnIncludeChineseChanged(bool value) => Save(s => s.IncludeChinese = value);
    partial void OnShowIndicatorChanged(bool value) => Save(s => s.ShowIndicator = value);
    partial void OnPlaySoundChanged(bool value) => Save(s => s.PlaySound = value);
    partial void OnModernToneStyleChanged(bool value) => Save(s => s.ModernToneStyle = value);
    partial void OnSpellCheckChanged(bool value) => Save(s => s.SpellCheck = value);
    partial void OnExcludedAppsChanged(string value) => Save(s => s.ExcludedApps = ProcessExclusions.ParseLines(value));

    private void Load(KeyboardSettings s)
    {
        _loading = true;
        Shortcut = s.Shortcut;
        Hotkey = s.Hotkey;
        IncludeChinese = s.IncludeChinese;
        ShowIndicator = s.ShowIndicator;
        PlaySound = s.PlaySound;
        ModernToneStyle = s.ModernToneStyle;
        SpellCheck = s.SpellCheck;
        ExcludedApps = string.Join(Environment.NewLine, s.ExcludedApps);
        _loading = false;
    }

    private void Save(Action<KeyboardSettings> apply)
    {
        if (!_loading) _store.Update(apply);
    }

    private void RefreshInstall()
    {
        var status = Module.Installer.GetStatus();
        IsInstalled = status.State is KeyboardInstallState.Installed or KeyboardInstallState.Outdated;
        CanInstall = status.State is KeyboardInstallState.NotInstalled or KeyboardInstallState.Outdated;
        InstallButton = status.State == KeyboardInstallState.Outdated ? "Update" : "Install";
        InstallDescription = status.State switch
        {
            KeyboardInstallState.NotBundled => "Not included in this build of Helm, so it cannot be installed from here.",
            KeyboardInstallState.NotInstalled => "Not installed. Installing makes Helm Keyboard the English keyboard (in place of US) and turns off Windows' own Ctrl+Shift.",
            KeyboardInstallState.Outdated => "Installed. A newer version came with this Helm.",
            _ => status.OldVersionsInUse > 0
                ? "Installed. Apps that were open before the last update type with the old version until you close them."
                : "Installed as the English keyboard. Removing it gives English its US keyboard back.",
        };
        OnPropertyChanged(nameof(HasChinese));
    }

    private void RefreshApps()
    {
        Apps.Clear();
        foreach (var app in Module.Apps.All()) Apps.Add(new KeyboardAppRow(Module.Apps, app));
        OnPropertyChanged(nameof(HasApps));
    }
}
