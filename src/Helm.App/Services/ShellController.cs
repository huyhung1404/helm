using System.Windows;
using Helm.App.Views;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Services;

/// <summary>Owns the lifetime of the main window and tray: show/hide, theme, exit.</summary>
internal sealed class ShellController(
    IServiceProvider services,
    ThemeService theme,
    TrayService tray,
    SearchService search,
    ISettingsStoreFactory settings)
{
    private readonly ISettingsStore<GeneralSettings> _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
    private MainWindow? _window;
    private bool _exiting;

    public bool IsExiting => _exiting;

    public void Initialize(bool showWindow)
    {
        theme.Apply(_general.Current.Theme, null);
        _general.Changed += (_, s) => Application.Current.Dispatcher.BeginInvoke(() => theme.Apply(s.Theme, _window));

        tray.Create(ShowMainWindow, () => _ = ExitAsync());
        _window = services.GetRequiredService<MainWindow>();
        if (showWindow) ShowMainWindow();
    }

    public void OnModulesStarted()
    {
        tray.RefreshMenu();
        search.Invalidate();
        _ = services.GetRequiredService<Core.Services.IUpdateService>().CheckAsync(CancellationToken.None);

        // An installed build owns the startup task: make sure it targets the stable launcher, not an old folder.
        var location = services.GetRequiredService<Core.Services.IAppLocation>();
        if (location.IsInstalled)
            _ = services.GetRequiredService<Core.Services.IStartupTaskService>().EnsurePathAsync(location.LauncherPath);
    }

    public void ShowMainWindow()
    {
        if (_window is null || _exiting) return;
        var firstShow = !_window.IsLoaded;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        // Bring to front even when another app has the foreground.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
        if (firstShow) theme.Apply(_general.Current.Theme, _window);
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _window?.SavePlacement();
        _window?.Close();
        await ((App)Application.Current).ShutdownGracefullyAsync().ConfigureAwait(true);
    }

    /// <summary>Starts a fresh instance and exits this one (used after "Reset all settings").</summary>
    public async Task RestartAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _window?.Close();
        var app = (App)Application.Current;
        await app.Services.GetRequiredService<Core.Modules.IModuleHost>().StopAllAsync().ConfigureAwait(true);
        app.ReleaseSingleInstance();
        services.GetRequiredService<Core.Services.IProcessLauncher>().StartNewInstance();
        await app.ShutdownGracefullyAsync().ConfigureAwait(true);
    }
}
