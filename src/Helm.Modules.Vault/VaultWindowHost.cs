using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Helm.Core.Desktop;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Vault.ViewModels;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Vault;

/// <summary>Owns the single vault window: created on first use, hidden rather than closed, closed for good when the module is turned off.</summary>
public sealed class VaultWindowHost
{
    private readonly VaultModule _module;
    private readonly VaultAppViewModel _app;
    private readonly IWindowService _windows;
    private readonly ISettingsStore<VaultSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private bool _closingForGood;

    public VaultWindowHost(VaultModule module, VaultAppViewModel app, IWindowService windows, ISettingsStoreFactory settings, IUiDispatcher ui,
        ILogger<VaultWindowHost> logger)
    {
        _module = module;
        _app = app;
        _windows = windows;
        _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
        _ui = ui;
        _logger = logger;
        module.PropertyChanged += OnModuleChanged;
        _settings.Changed += (_, _) => _ui.Post(ApplyCaptureProtection);
    }

    public Views.VaultWindow? Window { get; private set; }

    public void Show()
    {
        if (!_module.IsEnabled) return;
        var window = Window ??= Create();
        // Take the focus only when the user is in Helm (clicked "Open vault"); never from another app's back.
        var helmActive = Application.Current?.Windows.OfType<Window>().Any(w => w.IsActive) == true;
        window.ShowActivated = helmActive;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (!helmActive) return;
        window.Activate();
        window.FocusFirstInput();
    }

    private Views.VaultWindow Create()
    {
        var window = new Views.VaultWindow(_app);
        window.SourceInitialized += (_, _) => ApplyCaptureProtection();
        window.Closing += OnClosing;
        return window;
    }

    private void ApplyCaptureProtection()
    {
        if (Window is null) return;
        var hwnd = new WindowInteropHelper(Window).Handle;
        if (hwnd == 0) return;
        if (!_windows.SetExcludedFromCapture(hwnd, _settings.Current.ProtectFromScreenCapture))
            _logger.LogInformation("This Windows version cannot hide the vault from screen capture");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingForGood || Application.Current?.Dispatcher.HasShutdownStarted != false) return;
        e.Cancel = true;
        Window?.Hide();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IModule.IsEnabled) || _module.IsEnabled) return;
        _ui.Post(() =>
        {
            if (Window is null) return;
            _closingForGood = true;
            Window.Close();
            Window = null;
            _closingForGood = false;
        });
    }
}

/// <summary>The Home tile opens the vault itself (or the page, where the Enable toggle is, while it is off).</summary>
public sealed class VaultLauncher(VaultModule module, VaultWindowHost host, IShellNavigation navigation) : IModuleLauncher
{
    public string ModuleId => VaultModule.ModuleId;

    public Task LaunchAsync()
    {
        if (module.IsEnabled) host.Show();
        else navigation.ShowPage(typeof(VaultPage));
        return Task.CompletedTask;
    }
}

/// <summary>The settings page: the module (for the Enable card) and the shared settings view model.</summary>
public sealed class VaultPageViewModel(VaultModule module, VaultSettingsViewModel settings)
{
    public VaultModule Module { get; } = module;

    public VaultSettingsViewModel Settings { get; } = settings;
}
