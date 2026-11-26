using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Helm.App.Android.ViewModels;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.General;
using Helm.Home;
using Helm.Shell.Home;
using Helm.Shell.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.App.Android.Services;

/// <summary>Each tool's own picture on Home and General: the one its module already draws in the drawer.</summary>
internal sealed class AndroidToolIcons(IModuleHost<IAndroidModule> modules) : IToolIcons
{
    public IImage? For(string moduleId) => modules.Find(moduleId)?.IconImage;
}

/// <summary>Where a tap on Home goes on the phone: the tool's own action, else its content page, else its settings.</summary>
internal sealed class AndroidHomeLinks(IModuleHost<IAndroidModule> modules, IEnumerable<IModuleLauncher> launchers, ShellNavigator navigator,
    IProcessLauncher launcher, IServiceProvider services) : IHomeLinks
{
    public void OpenTool(string moduleId)
    {
        var tool = launchers.FirstOrDefault(l => l.ModuleId == moduleId);
        if (tool is not null)
        {
            _ = tool.LaunchAsync();
            return;
        }
        if (modules.Find(moduleId) is { } module) navigator.GoModuleContent(module);
    }

    public void OpenGeneral(string part)
    {
        var general = services.GetRequiredService<GeneralComposer>().Model;
        if (general.Sections.FirstOrDefault(s => s.Id == part) is { } section) general.Selected = section;
        navigator.GoGeneral();
    }

    /// <summary>The release page of the running version (its notes are the CHANGELOG section).</summary>
    public void OpenWhatsNew() => launcher.OpenUrl(AppInfo.ReleaseNotesUrl);
}

/// <summary>What General's own rows do on the phone: the theme, sharing the latest log, reset.</summary>
internal sealed class AndroidGeneralPlatform : ObservableObject, IGeneralPlatform
{
    private readonly ISettingsStoreFactory _settings;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly ThemeService _themes;
    private readonly AndroidLauncher _launcher;
    private readonly IDialogService _dialogs;
    private readonly ShellNavigator _navigator;
    private readonly ILogger<AndroidGeneralPlatform> _logger;
    private int _themeIndex;

    public AndroidGeneralPlatform(ISettingsStoreFactory settings, ThemeService themes, AndroidLauncher launcher, IDialogService dialogs,
        ShellNavigator navigator, ILogger<AndroidGeneralPlatform> logger)
    {
        _settings = settings;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        _themes = themes;
        _launcher = launcher;
        _dialogs = dialogs;
        _navigator = navigator;
        _logger = logger;
        _themeIndex = (int)_general.Current.Theme;
    }

    /// <summary>A phone has no folder to open: the logs are shared instead.</summary>
    public string SettingsFolder => "";

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (!SetProperty(ref _themeIndex, value)) return;
            var theme = (AppTheme)Math.Clamp(value, 0, 2);
            _general.Update(s => s.Theme = theme);
            _themes.Apply(theme);
        }
    }

    public bool RunAtStartup { get => false; set { } }

    public bool StartMinimized { get => false; set { } }

    public bool RunAsAdministrator => false;

    public void OpenSettingsFolder() { }

    /// <summary>Shares the newest log file (e.g. to send it to yourself), since Android has no folder to open.</summary>
    public void OpenLogs()
    {
        var latest = Directory.Exists(_settings.Paths.LogsDirectory)
            ? new DirectoryInfo(_settings.Paths.LogsDirectory).EnumerateFiles("helm-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (latest is null)
        {
            _ = _dialogs.ConfirmAsync("No log file yet", "There is no log file to share yet.", "OK");
            return;
        }
        _launcher.ShareFile(latest.FullName, "text/plain", "Share Helm log");
    }

    public async Task ResetAllAsync()
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
            await _dialogs.ConfirmAsync("Could not reset settings", ex.Message, "OK").ConfigureAwait(true);
        }
    }
}
