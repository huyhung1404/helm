using Avalonia.Threading;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Shell;

/// <summary>Owns the main window and the tray: show and hide, theme, exit, restart.</summary>
internal sealed class ShellController(
    IServiceProvider services,
    AppShellHost host,
    ThemeService theme,
    TrayService tray,
    ISettingsStoreFactory settings)
{
    private readonly ISettingsStore<GeneralSettings> _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
    private MainWindow? _window;
    private bool _exiting;

    public bool IsExiting => _exiting;

    /// <summary>The main window is on screen (not hidden in the tray, not minimized).</summary>
    public bool IsMainWindowShown => _window is { IsVisible: true } w && w.WindowState != Avalonia.Controls.WindowState.Minimized;

    public void Initialize(bool showWindow)
    {
        theme.Apply(_general.Current.Theme);
        _general.Changed += (_, s) => Dispatcher.UIThread.Post(() => theme.Apply(s.Theme));

        tray.Create(ShowMainWindow, () => _ = ExitAsync());
        _window = new MainWindow(host.Shell, _general, services.GetRequiredService<IModuleHost>(), () => _exiting, StartPage());
        host.MainWindow = _window;
        // Quicker pulls while the window is shown, so other devices' changes show up within seconds.
        var engine = services.GetRequiredService<Core.Sync.SyncEngine>();
        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Visual.IsVisibleProperty)
                engine.SetPollInterval(_window.IsVisible ? Core.Sync.SyncEngine.ForegroundPollInterval : Core.Sync.SyncEngine.BackgroundPollInterval);
        };
        if (showWindow) ShowMainWindow();
    }

    public void OnModulesStarted()
    {
        tray.RefreshMenu();
        services.GetService<SettingsSearch>()?.Invalidate();
        // Update checks start 30 s later in the background so launch is never slowed down.
        services.GetRequiredService<Updates.VelopackUpdateService>().StartBackgroundChecks();

        // An installed build owns the startup task: make sure it targets the stable launcher, not an old folder.
        var location = services.GetRequiredService<IAppLocation>();
        if (location.IsInstalled)
            _ = services.GetRequiredService<IStartupTaskService>().EnsurePathAsync(location.LauncherPath);
    }

    public void ShowMainWindow()
    {
        if (_window is null || _exiting) return;
        AppShellHost.Bring(_window);
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _window?.SavePlacement();
        _window?.Close();
        await App.Current.ShutdownGracefullyAsync().ConfigureAwait(true);
    }

    /// <summary>Starts a fresh instance and exits this one (used after "Reset all settings").</summary>
    public async Task RestartAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _window?.Close();
        await services.GetRequiredService<IModuleHost>().StopAllAsync().ConfigureAwait(true);
        App.Current.ReleaseSingleInstance();
        services.GetRequiredService<IProcessLauncher>().StartNewInstance();
        await App.Current.ShutdownGracefullyAsync().ConfigureAwait(true);
    }

    /// <summary>"--page X" (dev/testing): opens that page's tab in front: a page key, a tool's id or name.</summary>
    private string? StartPage()
    {
        var args = App.Args;
        var index = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return null;
        var name = args[index + 1];
        var catalog = host.Shell.Catalog;
        if (catalog.Knows(name.ToLowerInvariant())) return name.ToLowerInvariant();
        return catalog.Tools.FirstOrDefault(p => p.Title.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(p.Title.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))?.Key ?? Helm.Shell.Tabs.ShellPages.Home;
    }
}
