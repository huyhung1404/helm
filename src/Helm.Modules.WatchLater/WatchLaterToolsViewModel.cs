using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.WatchLater;

/// <summary>
/// The Windows-only part of the settings page: where downloads go, phone requests, browser cookies, and the yt-dlp and
/// ffmpeg that do the downloading. UI thread only.
/// </summary>
public sealed partial class WatchLaterToolsViewModel : ObservableObject
{
    private readonly YtDlpTools _tools;
    private readonly PcDownloads _downloads;
    private readonly ISettingsStore<WatchLaterSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger<WatchLaterToolsViewModel> _logger;
    private readonly bool _loading;

    [ObservableProperty] private string _ytDlpStatus = "";
    [ObservableProperty] private string _ffmpegStatus = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _downloadRequests;
    [ObservableProperty] private int _cookiesIndex;
    [ObservableProperty] private string _folder = "";

    private static readonly string[] CookieBrowsers = ["", "firefox", "chrome", "edge", "brave"];

    public WatchLaterToolsViewModel(YtDlpTools tools, PcDownloads downloads, ISettingsStoreFactory settings, IUiDispatcher ui,
        IProcessLauncher launcher, ILogger<WatchLaterToolsViewModel> logger)
    {
        _tools = tools;
        _downloads = downloads;
        _settings = settings.Get<WatchLaterSettings>(WatchLaterIds.ModuleId);
        _ui = ui;
        _launcher = launcher;
        _logger = logger;
        _loading = true;
        var s = _settings.Current;
        DownloadRequests = s.DownloadRequests;
        CookiesIndex = Math.Max(0, Array.IndexOf(CookieBrowsers, s.CookiesBrowser));
        Folder = downloads.Folder;
        _loading = false;
        _tools.Changed += (_, _) => _ui.Post(() => _ = RefreshAsync());
        _ = RefreshAsync();
    }

    public IReadOnlyList<string> CookieNames { get; } = ["Don't use", "Firefox", "Chrome", "Edge", "Brave"];

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool CanInstallFfmpeg => !IsBusy && !_tools.HasFfmpeg;

    public bool CanRemoveFfmpeg => !IsBusy && _tools.OwnsFfmpeg;

    public bool HasCustomFolder => _settings.Current.DownloadFolder is { Length: > 0 };

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallFfmpeg));
        OnPropertyChanged(nameof(CanRemoveFfmpeg));
    }

    partial void OnDownloadRequestsChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.DownloadRequests = value);
    }

    partial void OnCookiesIndexChanged(int value)
    {
        if (!_loading && value >= 0 && value < CookieBrowsers.Length) _settings.Update(s => s.CookiesBrowser = CookieBrowsers[value]);
    }

    /// <summary>A folder picked with the folder dialog (view glue).</summary>
    public void SetFolder(string folder)
    {
        _settings.Update(s => s.DownloadFolder = folder);
        Folder = _downloads.Folder;
        OnPropertyChanged(nameof(HasCustomFolder));
    }

    [RelayCommand]
    private void ResetFolder()
    {
        _settings.Update(s => s.DownloadFolder = null);
        Folder = _downloads.Folder;
        OnPropertyChanged(nameof(HasCustomFolder));
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_downloads.Folder);
            _launcher.OpenFolder(_downloads.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not open the folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UpdateYtDlpAsync()
    {
        IsBusy = true;
        YtDlpStatus = _tools.HasYtDlp ? "Checking for an update…" : "Downloading yt-dlp…";
        try
        {
            Message = await _tools.UpdateAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updating yt-dlp failed");
            Message = $"Could not update yt-dlp: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task InstallFfmpegAsync()
    {
        IsBusy = true;
        var progress = new Progress<double>(p => FfmpegStatus = $"Downloading ffmpeg… {p * 100:0} %");
        FfmpegStatus = "Downloading ffmpeg…";
        try
        {
            await _tools.InstallFfmpegAsync(progress, CancellationToken.None).ConfigureAwait(true);
            Message = "ffmpeg is installed: every quality can be downloaded now.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Installing ffmpeg failed");
            Message = $"Could not install ffmpeg: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RemoveFfmpegAsync()
    {
        _tools.RemoveFfmpeg();
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    private async Task RefreshAsync()
    {
        try
        {
            var version = await _tools.VersionAsync(CancellationToken.None).ConfigureAwait(true);
            YtDlpStatus = version is null
                ? "Not installed yet. It is downloaded (about 18 MB) the first time you download a video."
                : $"Version {version}. It updates itself every few days, because sites change often.";
            FfmpegStatus = _tools.FfmpegFolder is not { } ffmpeg
                ? "Not installed. Without it YouTube offers only low qualities (often 360p) and audio. About 130 MB to download."
                : _tools.OwnsFfmpeg ? "Installed. Every quality can be downloaded." : $"Found in {ffmpeg}. Every quality can be downloaded.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking the download tools failed");
        }
        OnPropertyChanged(nameof(CanInstallFfmpeg));
        OnPropertyChanged(nameof(CanRemoveFfmpeg));
    }
}
