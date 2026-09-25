using System.Diagnostics;
using Helm.App.Services;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Helm.App.Updates;

/// <summary>
/// <see cref="IUpdateService"/> backed by Velopack and GitHub Releases (huyhung1404/helm). Checks 30 s after start and
/// then every N hours; downloads in the background when allowed; never throws into the UI — failures become
/// <see cref="UpdateState.Failed"/> with a quiet <see cref="ErrorMessage"/>.
/// </summary>
internal sealed class VelopackUpdateService : IUpdateService, IDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly ISettingsStoreFactory _settings;
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly IModuleHost _modules;
    private readonly TrayService _tray;
    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly UpdateStateMachine _machine = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _installedChannel;
    private UpdateManager? _manager;
    private string? _managerKey;
    private UpdateInfo? _pending;
    private string? _notifiedVersion;

    public VelopackUpdateService(ISettingsStoreFactory settings, IModuleHost modules, TrayService tray, ILogger<VelopackUpdateService> logger)
    {
        _settings = settings;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        _modules = modules;
        _tray = tray;
        _logger = logger;

        try
        {
            var locator = VelopackLocator.Current;
            _installedChannel = locator.Channel;
            NotInstalledReason = UpdatePolicy.NotInstalledReason(
                locator.CurrentlyInstalledVersion is not null, locator.IsPortable, locator.UpdateExePath, File.Exists);
            if (locator.CurrentlyInstalledVersion is { } v) CurrentVersion = v.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Velopack locator unavailable");
            NotInstalledReason = UpdatePolicy.NotInstalledReason(false, false, null, File.Exists);
        }

        if (NotInstalledReason is not null)
        {
            _machine.TryFire(UpdateTrigger.NotInstalled);
            LastResult = UpdateCheckResult.NotInstalled(NotInstalledReason);
        }
        _machine.Changed += (_, _) => RaiseChanged();
        _general.Changed += (_, _) => _managerKey = null; // channel/source/token may have changed
    }

    public string CurrentVersion { get; } = AppInfo.Version;

    public bool IsInstalled => NotInstalledReason is null;

    public string? NotInstalledReason { get; }

    public UpdateState State => _machine.State;

    public DateTimeOffset? LastChecked => _general.Current.LastUpdateCheck;

    public UpdateCheckResult? LastResult { get; private set; }

    public int DownloadProgress { get; private set; }

    public string? ErrorMessage { get; private set; }

    public event EventHandler? StateChanged;

    /// <summary>First check after <see cref="StartupDelay"/>, then every CheckIntervalHours. Never blocks the caller.</summary>
    public void StartBackgroundChecks()
    {
        if (!IsInstalled)
        {
            _logger.LogInformation("Automatic updates disabled: {Reason}", NotInstalledReason);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartupDelay, _lifetime.Token).ConfigureAwait(false);
                while (!_lifetime.IsCancellationRequested)
                {
                    await CheckAsync(_lifetime.Token).ConfigureAwait(false);
                    var hours = Math.Clamp(_general.Current.Updates.CheckIntervalHours, 1, 24 * 7);
                    await Task.Delay(TimeSpan.FromHours(hours), _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        if (!IsInstalled) return LastResult ?? UpdateCheckResult.NotInstalled(NotInstalledReason ?? string.Empty);
        if (State is UpdateState.Downloading or UpdateState.Downloaded or UpdateState.Applying) return LastResult ?? UpdateCheckResult.UpToDate();
        if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false)) return LastResult ?? UpdateCheckResult.UpToDate();

        try
        {
            _machine.TryFire(UpdateTrigger.CheckStarted);
            ErrorMessage = null;
            var manager = GetManager();

            if (manager.UpdatePendingRestart is { } alreadyDownloaded)
            {
                LastResult = UpdateCheckResult.Available(alreadyDownloaded.Version.ToString(), alreadyDownloaded.NotesMarkdown, alreadyDownloaded.Size);
                _machine.TryFire(UpdateTrigger.DownloadCompleted);
                OnDownloaded(alreadyDownloaded.Version.ToString());
                return LastResult;
            }

            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (info is null)
            {
                _pending = null;
                LastResult = UpdateCheckResult.UpToDate();
                _machine.TryFire(UpdateTrigger.NoUpdate);
            }
            else
            {
                _pending = info;
                var target = info.TargetFullRelease;
                LastResult = UpdateCheckResult.Available(target.Version.ToString(), target.NotesMarkdown, target.Size);
                _machine.TryFire(UpdateTrigger.UpdateFound);
                _logger.LogInformation("Update available: {Version}", target.Version);
                NotifyAvailable(target.Version.ToString());
                if (_general.Current.Updates.AutoDownload) _ = DownloadAsync(null, _lifetime.Token);
            }
            return LastResult;
        }
        catch (OperationCanceledException)
        {
            _machine.TryFire(UpdateTrigger.Failed);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            ErrorMessage = Describe(ex);
            LastResult = UpdateCheckResult.Fail(ErrorMessage);
            _machine.TryFire(UpdateTrigger.Failed);
            return LastResult;
        }
        finally
        {
            _general.Update(s => s.LastUpdateCheck = DateTimeOffset.Now);
            _busy.Release();
            RaiseChanged();
        }
    }

    public async Task DownloadAsync(IProgress<int>? progress, CancellationToken ct)
    {
        if (_pending is not { } info || !_machine.TryFire(UpdateTrigger.DownloadStarted)) return;
        try
        {
            DownloadProgress = 0;
            await GetManager().DownloadUpdatesAsync(info, p =>
            {
                DownloadProgress = p;
                progress?.Report(p);
                RaiseChanged();
            }, ct).ConfigureAwait(false);
            _machine.TryFire(UpdateTrigger.DownloadCompleted);
            OnDownloaded(info.TargetFullRelease.Version.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update download failed");
            ErrorMessage = Describe(ex);
            _machine.TryFire(UpdateTrigger.Failed);
            // Keep the "available" result so the user can retry the download.
            _machine.TryFire(UpdateTrigger.CheckStarted);
            _machine.TryFire(UpdateTrigger.UpdateFound);
        }
    }

    public async Task ApplyAndRestartAsync()
    {
        if (!_machine.TryFire(UpdateTrigger.ApplyStarted)) return;
        var manager = GetManager();
        VelopackAsset? asset = _pending?.TargetFullRelease ?? manager.UpdatePendingRestart;
        if (asset is null)
        {
            ErrorMessage = "The downloaded update could not be found; check again.";
            _machine.TryFire(UpdateTrigger.Failed);
            return;
        }

        _logger.LogInformation("Applying update {Version}: disabling modules first", asset.Version);
        // No hook, topmost window or overlay may outlive this process.
        await _modules.StopAllAsync().ConfigureAwait(true);
        _settings.FlushAll();
        _tray.Dispose();
        await Serilog.Log.CloseAndFlushAsync().ConfigureAwait(true);

        try
        {
            // Update.exe inherits our elevation, so the restarted Helm is elevated without a UAC prompt.
            manager.ApplyUpdatesAndRestart(asset, ["--updated"]);
        }
        catch (Exception ex)
        {
            VelopackLifecycle.HookLog.Write($"ApplyUpdatesAndRestart failed ({ex.Message}); restarting elevated ourselves.");
            manager.WaitExitThenApplyUpdates(asset, silent: true, restart: false, ["--updated"]);
            StartElevatedRelauncher(VelopackLocator.Current.RootAppDir);
            Environment.Exit(0);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private UpdateManager GetManager()
    {
        var u = _general.Current.Updates;
        var key = $"{u.Channel}|{u.SourceOverride}|{u.GitHubToken?.Length}";
        if (_manager is not null && _managerKey == key) return _manager;

        var options = new UpdateOptions
        {
            ExplicitChannel = UpdatePolicy.ChannelName(u.Channel),
            AllowVersionDowngrade = UpdatePolicy.AllowDowngrade(u.Channel, _installedChannel),
        };
        _manager = new UpdateManager(CreateSource(u), options);
        _managerKey = key;
        _pending = null;
        return _manager;
    }

    private static IUpdateSource CreateSource(UpdateSettings u)
    {
        if (!string.IsNullOrWhiteSpace(u.SourceOverride))
        {
            var source = u.SourceOverride.Trim();
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                return new SimpleWebSource(uri);
            var path = Uri.TryCreate(source, UriKind.Absolute, out var fileUri) && fileUri.IsFile ? fileUri.LocalPath : source;
            return new SimpleFileSource(new DirectoryInfo(path));
        }
        var token = string.IsNullOrWhiteSpace(u.GitHubToken) ? null : u.GitHubToken.Trim();
        return new GithubSource(AppInfo.RepositoryUrl, token, UpdatePolicy.IncludePrereleases(u.Channel), null);
    }

    private void NotifyAvailable(string version)
    {
        if (_notifiedVersion == version) return;
        _notifiedVersion = version;
        var auto = _general.Current.Updates.AutoDownload;
        _tray.ShowNotification("Helm update available", auto
            ? $"Version {version} is downloading in the background."
            : $"Version {version} is available. Open General → Updates to download it.");
    }

    private void OnDownloaded(string version)
    {
        _tray.SetUpdateReady(version, () => _ = ApplyAndRestartAsync());
        _tray.ShowNotification("Helm update ready", $"Version {version} is downloaded. Choose \"Restart to update\" when convenient.");
        RaiseChanged();
    }

    /// <summary>Fallback restart: wait for Update.exe (of this install) to finish, then start the stable launcher elevated.</summary>
    private void StartElevatedRelauncher(string? rootAppDir)
    {
        var launcher = VelopackLifecycle.StableLauncherPath() ?? Environment.ProcessPath;
        if (launcher is null || rootAppDir is null) return;
        var script =
            $"$root = '{rootAppDir.Replace("'", "''")}'; " +
            "Start-Sleep -Seconds 2; " +
            "Get-Process -Name Update -ErrorAction SilentlyContinue | Where-Object { $_.Path -like \"$root*\" } | Wait-Process -Timeout 120; " +
            $"Start-Process -FilePath '{launcher.Replace("'", "''")}' -ArgumentList '--updated'";
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -Command \"{script}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            VelopackLifecycle.HookLog.Write($"Could not schedule the elevated restart: {ex.Message}");
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException http when http.StatusCode is System.Net.HttpStatusCode.Forbidden =>
            "GitHub refused the request (rate limit?). Add a GitHub token in Updates or try again later.",
        HttpRequestException => $"Could not reach the update server: {ex.Message}",
        TaskCanceledException => "The update server did not respond in time.",
        _ => ex.Message,
    };

    private void RaiseChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
