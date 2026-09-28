using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.App.Android.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;
using Helm.Shell.ViewModels;
using Microsoft.Extensions.Logging;

namespace Helm.App.Android.ViewModels;

/// <summary>
/// General on Android. Updates and Sync are the same view models as on Windows; the Windows-only rows
/// (administrator mode, run at startup, tray, uninstall cleanup, open folders) are replaced by Android equivalents.
/// </summary>
public sealed partial class GeneralViewModel : ObservableObject
{
    private readonly ISettingsStoreFactory _settings;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly ThemeService _themes;
    private readonly AndroidLauncher _launcher;
    private readonly IDialogService _dialogs;
    private readonly ILogger<GeneralViewModel> _logger;
    private readonly bool _loading;

    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private string? _statusMessage;

    public GeneralViewModel(
        ISettingsStoreFactory settings,
        ThemeService themes,
        AndroidLauncher launcher,
        UpdatesViewModel updates,
        SyncViewModel sync,
        IDialogService dialogs,
        ILogger<GeneralViewModel> logger)
    {
        _settings = settings;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        _themes = themes;
        _launcher = launcher;
        _dialogs = dialogs;
        _logger = logger;
        Updates = updates;
        Sync = sync;

        _loading = true;
        _theme = _general.Current.Theme;
        _loading = false;
    }

    public UpdatesViewModel Updates { get; }

    public SyncViewModel Sync { get; }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnThemeChanged(AppTheme value)
    {
        if (_loading) return;
        _general.Update(s => s.Theme = value);
        _themes.Apply(value);
    }

    /// <summary>Shares the newest log file (e.g. to send it to yourself), since Android has no folder to open.</summary>
    [RelayCommand]
    private void ShareLogs()
    {
        var latest = Directory.Exists(_settings.Paths.LogsDirectory)
            ? new DirectoryInfo(_settings.Paths.LogsDirectory).EnumerateFiles("helm-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (latest is null)
        {
            StatusMessage = "There is no log file yet.";
            return;
        }
        StatusMessage = null;
        _launcher.ShareFile(latest.FullName, "text/plain", "Share Helm log");
    }

    [RelayCommand]
    private async Task ResetAllSettingsAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Reset all settings?",
            "Every setting and preference on this phone will be deleted and Helm will restart with defaults. Synced data stays on the server.",
            "Reset and restart").ConfigureAwait(true);
        if (!confirmed) return;

        try
        {
            _settings.SuspendWrites();
            if (Directory.Exists(_settings.Paths.SettingsDirectory))
                Directory.Delete(_settings.Paths.SettingsDirectory, recursive: true);
            _logger.LogInformation("All settings reset by user");
            _launcher.StartNewInstance();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reset failed");
            StatusMessage = $"Could not reset settings: {ex.Message}";
        }
    }
}
