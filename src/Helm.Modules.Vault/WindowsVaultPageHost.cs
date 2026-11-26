using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Core.Settings;
using Helm.Modules.Vault.Platform;
using Helm.Modules.Vault.Ui;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Modules.Vault;

/// <summary>
/// The vault page on Windows: the shared <see cref="VaultView"/>, plus what only Windows does: while the vault is on
/// screen Helm's window stays out of screenshots and screen sharing (when the setting is on), and leaving the page,
/// hiding Helm or minimizing it forgets what was typed and hides what was shown.
/// </summary>
internal sealed class WindowsVaultPageHost : IVaultPageHost
{
    private readonly VaultAppViewModel _app;
    private readonly VaultSettingsViewModel _settingsViewModel;
    private readonly VaultWindowsSettingsViewModel _windowsSettings;
    private readonly IWindowService _windows;
    private readonly ISettingsStore<VaultSettings> _settings;
    private readonly VaultView _view;
    private nint _protected;
    private Window? _window;

    public WindowsVaultPageHost(VaultModule module, VaultAppViewModel app, VaultSettingsViewModel settingsViewModel,
        VaultWindowsSettingsViewModel windowsSettings, IWindowService windows, ISettingsStoreFactory settings)
    {
        _app = app;
        _settingsViewModel = settingsViewModel;
        _windowsSettings = windowsSettings;
        _windows = windows;
        _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
        _view = new VaultView(module, app, settingsViewModel, this);
        _view.AttachedToVisualTree += (_, _) => OnShown();
        _view.DetachedFromVisualTree += (_, _) => OnLeft();
        _view.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && e.NewValue is false) Forget();
        };
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(Protect);
        app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VaultAppViewModel.Screen) && _window is not null) Dispatcher.UIThread.Post(Protect);
        };
    }

    /// <summary>The page the shell shows.</summary>
    public Control View => _view;

    public Control? CreatePlatformSettings() => AutoTypeSettingsCard.Create(_windowsSettings);

    public bool CanLockWithSystem => true;

    private void OnShown()
    {
        if (_window is not null) _window.PropertyChanged -= OnWindowChanged;
        _window = TopLevel.GetTopLevel(_view) as Window;
        if (_window is not null) _window.PropertyChanged += OnWindowChanged;
        Protect();
    }

    private void OnLeft()
    {
        if (_window is not null) _window.PropertyChanged -= OnWindowChanged;
        _window = null;
        Unprotect();
        Forget();
    }

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if ((e.Property == Window.WindowStateProperty && e.NewValue is WindowState.Minimized)
            || (e.Property == Visual.IsVisibleProperty && e.NewValue is false))
            Forget();
    }

    /// <summary>The page is no longer in front of the user: nothing typed or revealed stays for whoever comes next.</summary>
    private void Forget()
    {
        _app.ForgetTypedSecrets();
        _app.Items.HideRevealed();
        _settingsViewModel.ForgetTypedSecrets();
    }

    /// <summary>
    /// While the vault is on screen, Helm's window stays out of screenshots, recordings and screen sharing (when the
    /// setting is on); leaving the page gives the window back to capture.
    /// </summary>
    private void Protect()
    {
        if (_window?.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;
        // The page moved to another window: the old one goes back to capture.
        if (_protected != 0 && _protected != hwnd) Unprotect();
        var on = _settings.Current.ProtectFromScreenCapture;
        _windows.SetExcludedFromCapture(hwnd, on);
        _protected = on ? hwnd : 0;
    }

    private void Unprotect()
    {
        if (_protected == 0) return;
        _windows.SetExcludedFromCapture(_protected, false);
        _protected = 0;
    }
}
