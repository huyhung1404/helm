using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.App.Services;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.App.ViewModels;

internal sealed partial class GeneralViewModel : ObservableObject
{
    private readonly ISettingsStoreFactory _settings;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly IStartupTaskService _startup;
    private readonly IProcessLauncher _launcher;
    private readonly IAppLocation _location;
    private readonly IDialogService _dialogs;
    private readonly ShellController _shell;
    private readonly ILogger<GeneralViewModel> _logger;
    private bool _loading;

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private bool _runAtStartup;

    [ObservableProperty]
    private bool _isStartupBusy;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private string? _statusMessage;

    public GeneralViewModel(
        ISettingsStoreFactory settings,
        IStartupTaskService startup,
        IProcessLauncher launcher,
        IAppLocation location,
        UpdatesViewModel updates,
        IDialogService dialogs,
        ShellController shell,
        ILogger<GeneralViewModel> logger)
    {
        _settings = settings;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        _startup = startup;
        _launcher = launcher;
        _location = location;
        Updates = updates;
        _dialogs = dialogs;
        _shell = shell;
        _logger = logger;

        _loading = true;
        _theme = _general.Current.Theme;
        _startMinimized = _general.Current.StartMinimized;
        _loading = false;

        _ = LoadStartupStateAsync();
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    /// <summary>General → Updates section.</summary>
    public UpdatesViewModel Updates { get; }

    public string ElevationText => _launcher.IsElevated
        ? "Running as administrator — hooks and hotkeys also work on elevated windows."
        : "Not running as administrator — Helm cannot move or hook elevated windows.";

    public string SettingsFolder => _settings.Paths.SettingsDirectory;

    partial void OnThemeChanged(AppTheme value)
    {
        if (!_loading) _general.Update(s => s.Theme = value);
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (!_loading) _general.Update(s => s.StartMinimized = value);
    }

    partial void OnRunAtStartupChanged(bool value)
    {
        if (!_loading) _ = ApplyStartupAsync(value);
    }

    [RelayCommand]
    private void OpenSettingsFolder() => _launcher.OpenFolder(_settings.Paths.SettingsDirectory);

    [RelayCommand]
    private void OpenLogsFolder() => _launcher.OpenFolder(_settings.Paths.LogsDirectory);

    [RelayCommand]
    private async Task ResetAllSettingsAsync()
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
            await _shell.RestartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reset failed");
            StatusMessage = $"Could not reset settings: {ex.Message}";
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
        IsStartupBusy = true;
        try
        {
            if (enable) await _startup.EnableAsync(_location.LauncherPath).ConfigureAwait(true);
            else await _startup.DisableAsync().ConfigureAwait(true);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not change the startup task");
            StatusMessage = $"Could not {(enable ? "create" : "remove")} the startup task: {ex.Message}";
            _loading = true;
            RunAtStartup = !enable;
            _loading = false;
        }
        finally
        {
            IsStartupBusy = false;
        }
    }
}
