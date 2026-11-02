using System.Windows.Media;
using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Typing;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace Helm.Modules.Keyboard;

public sealed class KeyboardModule : HelmModuleBase
{
    public const string ModuleId = "keyboard";
    private const string HotkeyId = "switch";

    private readonly IHotkeyManager _hotkeys;
    private readonly IWindowService _windows;
    private readonly IInputMethods _inputMethods;
    private readonly IMonitorService _monitors;
    private readonly IBrowserSites _sites;
    private readonly LowLevelKeyboardHook _keyboard;
    private readonly ILogger<KeyboardModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private KeyboardEngine? _engine;
    private HotkeyRegistration? _registration;
    private bool _enabled;

    public KeyboardModule(ISettingsStoreFactory settings, KeyboardApps apps, KeyboardInstaller installer, IHotkeyManager hotkeys,
        IWindowService windows, IInputMethods inputMethods, IMonitorService monitors, IBrowserSites sites, LowLevelKeyboardHook keyboard, ILogger<KeyboardModule> logger)
    {
        Settings = settings.Get<KeyboardSettings>(ModuleId);
        Apps = apps;
        Installer = installer;
        _hotkeys = hotkeys;
        _windows = windows;
        _inputMethods = inputMethods;
        _monitors = monitors;
        _sites = sites;
        _keyboard = keyboard;
        _logger = logger;
        Settings.Changed += (_, _) => _ = ApplySettingsAsync();
    }

    public override string Id => ModuleId;
    public override string DisplayName => "Helm Keyboard";
    public override string Description => "Type Vietnamese with Telex, go round English, Vietnamese and Chinese with one shortcut, and let every app — and every web site — keep the keyboard you last used in it.";
    public override ModuleGroup Group => ModuleGroup.WindowsAndDesktop;
    public override SymbolRegular Icon => SymbolRegular.Keyboard24;
    public override ImageSource? IconImage => KeyboardLogo.Image;
    public override Type SettingsPageType => typeof(KeyboardPage);

    public override IReadOnlyList<HotkeyDefinition> Hotkeys =>
    [
        Settings.Current.Shortcut switch
        {
            // Modifier keys alone are written with no key, like the other press-and-hold shortcuts.
            SwitchShortcut.LeftCtrlShift => new HotkeyDefinition(ModuleId, HotkeyId, "Switch English / Vietnamese / Chinese (left Ctrl+Shift, pressed and let go)",
                new HotkeyGesture(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0)),
            SwitchShortcut.RightCtrl => new HotkeyDefinition(ModuleId, HotkeyId, "Switch English / Vietnamese / Chinese (right Ctrl, pressed and let go)",
                new HotkeyGesture(HotkeyModifiers.Ctrl, 0)),
            _ => new HotkeyDefinition(ModuleId, HotkeyId, "Switch English / Vietnamese / Chinese", Settings.Current.Hotkey),
        },
    ];

    public ISettingsStore<KeyboardSettings> Settings { get; }

    public KeyboardApps Apps { get; }

    public KeyboardInstaller Installer { get; }

    /// <summary>Raised (on any thread) after Helm Keyboard was installed, updated or removed.</summary>
    public event EventHandler? InstallChanged;

    public override async Task EnableAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _enabled = true;
            WriteTypingOptions();
            await StartAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task DisableAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _enabled = false;
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Installs (or updates) the input method and starts switching; false with the reason in StatusMessage.</summary>
    public Task<bool> InstallAsync() => ChangeInstallAsync(() => Installer.Install(), "install");

    public Task<bool> UninstallAsync() => ChangeInstallAsync(() => Installer.Uninstall(), "remove");

    private async Task<bool> ChangeInstallAsync(Action change, string what)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            StatusMessage = null;
            try
            {
                await Task.Run(change).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not {What} Helm Keyboard", what);
                StatusMessage = $"Could not {what} Helm Keyboard: {ex.Message}";
                return false;
            }
            finally
            {
                InstallChanged?.Invoke(this, EventArgs.Empty);
            }
            if (_enabled) await StartAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Under the gate.
    private async Task StartAsync()
    {
        if (_engine is not null) return;
        try
        {
            Installer.CleanUp();
            if (Installer.GetStatus().State == KeyboardInstallState.Outdated)
            {
                // Helm was updated: the input method next to it replaces the installed one.
                await Task.Run(Installer.Install).ConfigureAwait(false);
                InstallChanged?.Invoke(this, EventArgs.Empty);
            }
            if (Installer.GetStatus().State != KeyboardInstallState.Installed) return;
            _engine = await KeyboardEngine.StartAsync(_windows, _inputMethods, _monitors, _sites, Apps, _keyboard, _logger,
                KeyboardEngine.Options.From(Settings.Current)).ConfigureAwait(false);
            await RegisterHotkeyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Helm Keyboard could not start");
            StatusMessage = $"Helm Keyboard could not start: {ex.Message}";
        }
    }

    // Under the gate.
    private async Task StopAsync()
    {
        _registration?.Dispose();
        _registration = null;
        if (_engine is { } engine)
        {
            _engine = null;
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RegisterHotkeyAsync()
    {
        _registration?.Dispose();
        _registration = null;
        if (_engine is not { } engine || Settings.Current.Shortcut != SwitchShortcut.Hotkey)
        {
            NotifyHotkeysChanged();
            return;
        }
        var definition = Hotkeys[0];
        _registration = await _hotkeys.TryRegisterAsync(definition, engine.SwitchNext).ConfigureAwait(false);
        StatusMessage = _registration.IsRegistered ? null : $"The shortcut {definition.Gesture} is not active: {_registration.Error}";
        NotifyHotkeysChanged();
    }

    private async Task ApplySettingsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            WriteTypingOptions();
            if (_engine is null)
            {
                NotifyHotkeysChanged();
                return;
            }
            _engine.UpdateOptions(KeyboardEngine.Options.From(Settings.Current));
            var wanted = Settings.Current.Shortcut == SwitchShortcut.Hotkey ? Settings.Current.Hotkey : (HotkeyGesture?)null;
            if (_registration?.Definition.Gesture != wanted) await RegisterHotkeyAsync().ConfigureAwait(false);
            else NotifyHotkeysChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply Helm Keyboard settings");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The typing options, where the input method reads them each time an app starts typing with it.</summary>
    private void WriteTypingOptions()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyboardIds.SettingsKey);
            key.SetValue(KeyboardIds.ModernToneStyleValue, Settings.Current.ModernToneStyle ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(KeyboardIds.SpellCheckValue, Settings.Current.SpellCheck ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the typing options for Helm Keyboard");
        }
    }
}
