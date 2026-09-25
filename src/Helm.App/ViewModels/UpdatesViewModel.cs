using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.App.Services;
using Helm.Core.Services;
using Helm.Core.Settings;

namespace Helm.App.ViewModels;

/// <summary>General → Updates. All state comes from <see cref="IUpdateService"/>; failures are shown quietly.</summary>
internal sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly IProcessLauncher _launcher;
    private readonly IUiDispatcher _ui;
    private bool _loading;

    [ObservableProperty] private UpdateChannel _channel;
    [ObservableProperty] private bool _autoDownload;
    [ObservableProperty] private bool _autoInstallOnRestart;
    [ObservableProperty] private string _gitHubToken = string.Empty;
    [ObservableProperty] private bool _purgeDataOnUninstall;

    public UpdatesViewModel(IUpdateService updates, ISettingsStoreFactory settings, IProcessLauncher launcher, IUiDispatcher ui)
    {
        _updates = updates;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        _launcher = launcher;
        _ui = ui;

        _loading = true;
        var u = _general.Current.Updates;
        Channel = u.Channel;
        AutoDownload = u.AutoDownload;
        AutoInstallOnRestart = u.AutoInstallOnRestart;
        GitHubToken = u.GitHubToken ?? string.Empty;
        PurgeDataOnUninstall = _general.Current.PurgeDataOnUninstall;
        _loading = false;

        updates.StateChanged += (_, _) => _ui.Post(Refresh);
    }

    public IReadOnlyList<UpdateChannel> Channels { get; } = Enum.GetValues<UpdateChannel>();

    public string CurrentVersion => $"Helm v{_updates.CurrentVersion}";

    public bool IsInstalled => _updates.IsInstalled;

    public string? NotInstalledReason => _updates.NotInstalledReason;

    public UpdateState State => _updates.State;

    public bool IsChecking => State == UpdateState.Checking;

    public bool IsDownloading => State == UpdateState.Downloading;

    public bool CanDownload => State == UpdateState.UpdateAvailable || (State == UpdateState.Failed && _updates.LastResult?.Status == UpdateCheckStatus.UpdateAvailable);

    public bool IsDownloaded => State == UpdateState.Downloaded;

    public bool HasUpdate => _updates.LastResult?.Status == UpdateCheckStatus.UpdateAvailable && State is not UpdateState.UpToDate;

    public int Progress => _updates.DownloadProgress;

    public string? ErrorMessage => _updates.ErrorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string? SourceOverride => _general.Current.Updates.SourceOverride;

    public bool HasSourceOverride => !string.IsNullOrWhiteSpace(SourceOverride);

    public string StatusText => State switch
    {
        UpdateState.NotInstalled => "Updates unavailable (portable or development build)",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpdateAvailable => $"Version {_updates.LastResult?.Version} is available{SizeText()}",
        UpdateState.Downloading => $"Downloading version {_updates.LastResult?.Version}… {Progress}%",
        UpdateState.Downloaded => $"Version {_updates.LastResult?.Version} is ready — restart to update",
        UpdateState.Applying => "Applying the update…",
        UpdateState.Failed => "The last update attempt failed",
        UpdateState.UpToDate => "You're up to date",
        _ => "Not checked yet",
    };

    public string LastCheckedText => _updates.LastChecked is { } t ? $"Last checked {t.ToLocalTime():g}" : "Never checked";

    /// <summary>Release notes as plain text (the Markdown is simplified, not rendered).</summary>
    public string ReleaseNotes => ToPlainText(_updates.LastResult?.ReleaseNotes);

    public bool HasReleaseNotes => HasUpdate && !string.IsNullOrWhiteSpace(ReleaseNotes);

    [RelayCommand]
    private Task CheckAsync() => _updates.CheckAsync(CancellationToken.None);

    [RelayCommand]
    private Task DownloadAsync() => _updates.DownloadAsync(null, CancellationToken.None);

    [RelayCommand]
    private Task RestartToUpdateAsync() => _updates.ApplyAndRestartAsync();

    [RelayCommand]
    private void OpenReleases() => _launcher.OpenUrl(AppInfo.ReleasesUrl);

    partial void OnChannelChanged(UpdateChannel value) => Save(s => s.Updates.Channel = value);
    partial void OnAutoDownloadChanged(bool value) => Save(s => s.Updates.AutoDownload = value);
    partial void OnAutoInstallOnRestartChanged(bool value) => Save(s => s.Updates.AutoInstallOnRestart = value);
    partial void OnGitHubTokenChanged(string value) => Save(s => s.Updates.GitHubToken = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    partial void OnPurgeDataOnUninstallChanged(bool value) => Save(s => s.PurgeDataOnUninstall = value);

    private void Save(Action<GeneralSettings> apply)
    {
        if (!_loading) _general.Update(apply);
    }

    private void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(State), nameof(IsChecking), nameof(IsDownloading), nameof(CanDownload), nameof(IsDownloaded),
                     nameof(HasUpdate), nameof(Progress), nameof(ErrorMessage), nameof(HasError), nameof(StatusText),
                     nameof(LastCheckedText), nameof(ReleaseNotes), nameof(HasReleaseNotes), nameof(SourceOverride), nameof(HasSourceOverride),
                 })
            OnPropertyChanged(name);
    }

    private string SizeText() => _updates.LastResult?.SizeBytes is > 0 and var size ? $" ({size / 1024d / 1024d:0.0} MB)" : string.Empty;

    private static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var text = Regex.Replace(markdown, @"^\s{0,3}#{1,6}\s*", string.Empty, RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "• ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"(\*\*|__|`)", string.Empty);
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text.Trim();
    }
}
