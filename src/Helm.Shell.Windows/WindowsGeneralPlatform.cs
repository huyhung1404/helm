using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Home;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Shell.Windows;

/// <summary>What General's own rows do on Windows: the theme and "start minimized" settings, the startup task, the folders, reset.</summary>
internal sealed class WindowsGeneralPlatform : ObservableObject, IGeneralPlatform
{
    private readonly ISettingsStoreFactory _settings;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly IStartupTaskService _startup;
    private readonly IProcessLauncher _launcher;
    private readonly IAppLocation _location;
    private readonly IDialogService _dialogs;
    private readonly IShellWindowsHost _host;
    private readonly ILogger<WindowsGeneralPlatform> _logger;
    private bool _loading;

    private int _themeIndex;
    private bool _runAtStartup;
    private bool _startMinimized;

    public WindowsGeneralPlatform(ISettingsStoreFactory settings, IStartupTaskService startup, IProcessLauncher launcher, IAppLocation location,
        IDialogService dialogs, IShellWindowsHost host, ILogger<WindowsGeneralPlatform> logger)
    {
        _settings = settings;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        _startup = startup;
        _launcher = launcher;
        _location = location;
        _dialogs = dialogs;
        _host = host;
        _logger = logger;
        _themeIndex = (int)_general.Current.Theme;
        _startMinimized = _general.Current.StartMinimized;
        _ = LoadStartupStateAsync();
    }

    public string SettingsFolder => _settings.Paths.SettingsDirectory;

    public bool RunAsAdministrator => _launcher.IsElevated;

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (!SetProperty(ref _themeIndex, value)) return;
            _general.Update(s => s.Theme = (AppTheme)Math.Clamp(value, 0, 2));
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!SetProperty(ref _startMinimized, value)) return;
            _general.Update(s => s.StartMinimized = value);
        }
    }

    public bool RunAtStartup
    {
        get => _runAtStartup;
        set
        {
            if (!SetProperty(ref _runAtStartup, value) || _loading) return;
            _ = ApplyStartupAsync(value);
        }
    }

    public bool HasAiFeatures => false;

    public void OpenAiFeatures() { }

    public void OpenSettingsFolder() => _launcher.OpenFolder(_settings.Paths.SettingsDirectory);

    public void OpenLogs() => _launcher.OpenFolder(_settings.Paths.LogsDirectory);

    public async Task ResetAllAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Reset all settings?",
            "Every module setting, layout and preference will be deleted and Helm will restart with defaults.",
            "Reset and restart").ConfigureAwait(true);
        if (!confirmed) return;

        try
        {
            _settings.SuspendWrites();
            if (Directory.Exists(_settings.Paths.SettingsDirectory))
                Directory.Delete(_settings.Paths.SettingsDirectory, recursive: true);
            _logger.LogInformation("All settings reset by user");
            await _host.RestartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reset failed");
            await _dialogs.ConfirmAsync("Could not reset settings", ex.Message, "OK").ConfigureAwait(true);
        }
    }

    private async Task LoadStartupStateAsync()
    {
        var enabled = await Task.Run(_startup.IsEnabled).ConfigureAwait(true);
        _loading = true;
        RunAtStartup = enabled;
        _loading = false;
    }

    private async Task ApplyStartupAsync(bool enable)
    {
        try
        {
            if (enable) await _startup.EnableAsync(_location.LauncherPath).ConfigureAwait(true);
            else await _startup.DisableAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not change the startup task");
            _loading = true;
            RunAtStartup = !enable;
            _loading = false;
            await _dialogs.ConfirmAsync($"Could not {(enable ? "create" : "remove")} the startup task", ex.Message, "OK").ConfigureAwait(true);
        }
    }
}
