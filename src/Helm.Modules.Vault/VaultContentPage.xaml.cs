using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Helm.Core.Desktop;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Modules.Vault.Platform;
using Helm.Modules.Vault.Ui;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Modules.Vault;

/// <summary>
/// The vault, opened from the menu, Home and Utilities (see <see cref="IModuleContent"/>): the shared Avalonia
/// <see cref="VaultView"/> inside this WPF page, plus what only Windows does: while the vault is on screen Helm's window
/// stays out of screenshots and screen sharing (when the setting is on), and leaving the page, hiding Helm or minimizing
/// it forgets what was typed and hides what was shown.
/// </summary>
public partial class VaultContentPage : Page
{
    private readonly VaultAppViewModel _app;
    private readonly VaultSettingsViewModel _settingsViewModel;
    private readonly IWindowService _windows;
    private readonly ISettingsStore<VaultSettings> _settings;
    private nint _protected;
    private Window? _window;

    public VaultContentPage(VaultModule module, VaultAppViewModel app, VaultSettingsViewModel settingsViewModel, VaultWindowsSettingsViewModel windowsSettings,
        IWindowService windows, ISettingsStoreFactory settings)
    {
        _app = app;
        _settingsViewModel = settingsViewModel;
        _windows = windows;
        _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
        InitializeComponent();
        Host.Show(() => new VaultView(module, app, settingsViewModel, new WindowsHost(windowsSettings)));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) Forget();
        };
        _settings.Changed += (_, _) => Dispatcher.BeginInvoke(Protect);
        app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VaultAppViewModel.Screen) && IsLoaded) Dispatcher.BeginInvoke(Protect);
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Protect();
        if (_window is not null) _window.StateChanged -= OnWindowState;
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += OnWindowState;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null) _window.StateChanged -= OnWindowState;
        _window = null;
        Unprotect();
        Forget();
    }

    private void OnWindowState(object? sender, EventArgs e)
    {
        if (_window?.WindowState == WindowState.Minimized) Forget();
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
        if (!IsLoaded || Window.GetWindow(this) is not { } window) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
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

    /// <summary>What only Windows adds to the shared page.</summary>
    private sealed class WindowsHost(VaultWindowsSettingsViewModel windowsSettings) : IVaultPageHost
    {
        public Avalonia.Controls.Control? CreatePlatformSettings() => AutoTypeSettingsCard.Create(windowsSettings);

        public bool CanLockWithSystem => true;
    }
}
