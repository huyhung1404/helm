using System.ComponentModel;
using Avalonia.Controls;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Modules.Keyboard;
using Helm.Modules.Keyboard.Ui;

namespace Helm.Gallery;

/// <summary>Which Helm Keyboard a Gallery entry shows.</summary>
internal enum KeyboardStage
{
    /// <summary>Installed, a few apps and sites with their own keyboard.</summary>
    Installed,
    /// <summary>Not installed yet, no app remembered.</summary>
    NotInstalled,
    /// <summary>Installing failed; a shortcut of one's own.</summary>
    Problem,
}

/// <summary>The real Helm Keyboard view and view model over settings in a temporary folder and a pretend input method.</summary>
internal static class KeyboardDemo
{
    public static Control Create(FakeModule module, KeyboardStage stage) => Build(module, stage).View;

    /// <summary>The view with its view model and pretend tool (the UI tests drive them).</summary>
    public static (KeyboardView View, KeyboardViewModel ViewModel, PretendKeyboard Tool) Build(FakeModule module, KeyboardStage stage)
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var apps = new KeyboardApps(new MemorySynced<KeyboardAppMode>());
        var tool = new PretendKeyboard(module, new SettingsStoreFactory(new HelmPaths(dir)), apps,
            stage == KeyboardStage.NotInstalled ? KeyboardInstallState.NotInstalled : KeyboardInstallState.Installed);
        if (stage != KeyboardStage.NotInstalled)
        {
            apps.Remember("Zalo.exe", KeyboardMode.Vietnamese);
            apps.Remember("WeChat.exe", KeyboardMode.Chinese);
            apps.Remember(KeyboardApps.SiteKeyOf("facebook.com"), KeyboardMode.Vietnamese);
            apps.Remember(KeyboardApps.SiteKeyOf("bilibili.com"), KeyboardMode.Chinese);
        }
        if (stage == KeyboardStage.Problem)
        {
            tool.Settings.Update(s => s.Shortcut = SwitchShortcut.Hotkey);
            tool.StatusMessage = "Could not install Helm Keyboard: access to the input method folder was denied.";
        }
        var vm = new KeyboardViewModel(tool, new AvaloniaUi());
        return (new KeyboardView(vm, new Host()), vm, tool);
    }

    private sealed class Host : IKeyboardPageHost;
}

/// <summary>Helm Keyboard, pretended: Install and Remove only change the state shown. On/off follows the Gallery's switch.</summary>
internal sealed class PretendKeyboard : IKeyboard
{
    private readonly FakeModule _module;
    private KeyboardInstallState _state;
    private string? _status;

    public PretendKeyboard(FakeModule module, ISettingsStoreFactory settings, KeyboardApps apps, KeyboardInstallState state)
    {
        _module = module;
        _state = state;
        Apps = apps;
        Settings = settings.Get<KeyboardSettings>("keyboard");
        module.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, e);
    }

    public string Id => "keyboard";
    public string DisplayName => "Helm Keyboard";
    public string Description => "";
    public ModuleGroup Group => ModuleGroup.WindowsAndDesktop;

    public bool IsEnabled
    {
        get => _module.IsEnabled;
        set => _module.IsEnabled = value;
    }

    public string? StatusMessage
    {
        get => _status;
        set
        {
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage)));
        }
    }

    public ISettingsStore<KeyboardSettings> Settings { get; }

    public KeyboardApps Apps { get; }

    public bool HasChinese => true;

    public event EventHandler? InstallChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public KeyboardInstallStatus InstallStatus() => new(_state, 0);

    public Task<bool> InstallAsync() => Change(KeyboardInstallState.Installed);

    public Task<bool> UninstallAsync() => Change(KeyboardInstallState.NotInstalled);

    public Task EnableAsync(CancellationToken ct) => Task.CompletedTask;

    public Task DisableAsync() => Task.CompletedTask;

    private Task<bool> Change(KeyboardInstallState state)
    {
        _state = state;
        InstallChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(true);
    }
}
