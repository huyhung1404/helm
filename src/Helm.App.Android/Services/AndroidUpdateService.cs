using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Android.Content;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using AndroidSettings = Android.Provider.Settings;
using AndroidUri = Android.Net.Uri;

namespace Helm.App.Android.Services;

/// <summary>
/// Updates for the Android app from GitHub Releases: the newest release on the chosen channel that carries
/// <see cref="AssetName"/>. The APK is downloaded to the cache, checked against GitHub's SHA-256 digest and handed to
/// the system installer, which asks the user to confirm (Android never installs silently). Same states and settings
/// as the Windows updater, so General → Updates and the Home tile are shared.
/// </summary>
internal sealed class AndroidUpdateService : IUpdateService, IDisposable
{
    public const string AssetName = "Helm-android.apk";
    private const string ApkMimeType = "application/vnd.android.package-archive";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly UpdateStateMachine _machine = new();
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly ILogger<AndroidUpdateService> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _downloadDirectory;
    private ReleaseCandidate? _pending;
    private string? _downloadedFile;

    public AndroidUpdateService(ISettingsStoreFactory settings, ILogger<AndroidUpdateService> logger)
    {
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        _logger = logger;
        _downloadDirectory = Path.Combine(AndroidApp.Context.CacheDir!.AbsolutePath, "updates");
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Helm-Android", AppInfo.Version.Split('+')[0]));

#if !HELM_SIGNED
        NotInstalledReason = "This is a development build of Helm (not signed with the release key), so automatic updates are off. " +
                             "Install Helm-android.apk from GitHub releases to get updates.";
#endif
        if (NotInstalledReason is not null)
        {
            _machine.TryFire(UpdateTrigger.NotInstalled);
            LastResult = UpdateCheckResult.NotInstalled(NotInstalledReason);
        }
        _machine.Changed += (_, _) => RaiseChanged();
        DeleteStaleDownloads();
    }

    public string CurrentVersion { get; } = AppInfo.Version;

    public bool IsInstalled => NotInstalledReason is null;

    public string? NotInstalledReason { get; }

    public UpdateState State => _machine.State;

    public DateTimeOffset? LastChecked => _general.Current.LastUpdateCheck;

    public UpdateCheckResult? LastResult { get; private set; }

    public int DownloadProgress { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string ApplyActionText => "Install update";

    public string NotInstalledTitle => "Updates unavailable (development build)";

    public event EventHandler? StateChanged;

    /// <summary>First check after <see cref="StartupDelay"/>, then every CheckIntervalHours while Helm is open.</summary>
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

        var autoDownload = false;
        try
        {
            _machine.TryFire(UpdateTrigger.CheckStarted);
            ErrorMessage = null;

            var releases = await FetchReleasesAsync(ct).ConfigureAwait(false);
            var updates = _general.Current.Updates;
            var candidate = GitHubReleases.PickUpdate(releases, updates.Channel, CurrentVersion, AssetName);
            _general.Update(s => s.LastUpdateCheck = DateTimeOffset.UtcNow);

            if (candidate is null)
            {
                _pending = null;
                LastResult = UpdateCheckResult.UpToDate();
                _machine.TryFire(UpdateTrigger.NoUpdate);
                return LastResult;
            }

            _pending = candidate;
            LastResult = UpdateCheckResult.Available(candidate.Version, candidate.Notes, candidate.Asset.Size);
            _logger.LogInformation("Update {Version} available ({Size} bytes)", candidate.Version, candidate.Asset.Size);

            var file = ApkPath(candidate.Version);
            if (File.Exists(file) && await IsValidDownloadAsync(file, candidate, ct).ConfigureAwait(false))
            {
                _downloadedFile = file;
                _machine.TryFire(UpdateTrigger.DownloadCompleted);
            }
            else
            {
                _machine.TryFire(UpdateTrigger.UpdateFound);
                autoDownload = updates.AutoDownload;
            }
            return LastResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Update check failed");
            ErrorMessage = Describe(ex);
            LastResult = UpdateCheckResult.Fail(ErrorMessage);
            _machine.TryFire(UpdateTrigger.Failed);
            return LastResult;
        }
        finally
        {
            _busy.Release();
            RaiseChanged();
            if (autoDownload) _ = DownloadAsync(null, _lifetime.Token);
        }
    }

    public async Task DownloadAsync(IProgress<int>? progress, CancellationToken ct)
    {
        if (_pending is not { } candidate || !_machine.CanFire(UpdateTrigger.DownloadStarted)) return;
        if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false)) return;

        var file = ApkPath(candidate.Version);
        var partial = file + ".part";
        try
        {
            ErrorMessage = null;
            DownloadProgress = 0;
            _machine.TryFire(UpdateTrigger.DownloadStarted);
            Directory.CreateDirectory(_downloadDirectory);

            using var response = await _http.GetAsync(candidate.Asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? candidate.Asset.Size;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    var percent = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
                    if (percent != DownloadProgress)
                    {
                        DownloadProgress = percent;
                        progress?.Report(percent);
                        RaiseChanged();
                    }
                }
            }

            if (!await IsValidDownloadAsync(partial, candidate, ct).ConfigureAwait(false))
                throw new InvalidDataException("The downloaded update does not match the release (size or SHA-256). It was deleted; try again.");
            File.Move(partial, file, overwrite: true);
            _downloadedFile = file;
            DownloadProgress = 100;
            _machine.TryFire(UpdateTrigger.DownloadCompleted);
            _logger.LogInformation("Update {Version} downloaded to {File}", candidate.Version, file);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update download failed");
            ErrorMessage = ex is OperationCanceledException ? "The download was cancelled." : Describe(ex);
            _machine.TryFire(UpdateTrigger.Failed);
            TryDelete(partial);
        }
        finally
        {
            _busy.Release();
            RaiseChanged();
        }
    }

    /// <summary>
    /// Opens the system installer for the downloaded APK. The state stays Downloaded: the user may cancel the
    /// installer and tap again. When the update is installed, Android stops this process and Helm starts fresh.
    /// </summary>
    public Task ApplyAndRestartAsync()
    {
        if (State != UpdateState.Downloaded || _downloadedFile is not { } file || !File.Exists(file)) return Task.CompletedTask;
        var context = AndroidApp.Context;
        try
        {
            ErrorMessage = null;
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O && context.PackageManager?.CanRequestPackageInstalls() == false)
            {
                var settings = new Intent(AndroidSettings.ActionManageUnknownAppSources, AndroidUri.Parse($"package:{context.PackageName}"));
                settings.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(settings);
                ErrorMessage = "Allow “Install unknown apps” for Helm in the settings page that just opened, then tap Install update again.";
                return Task.CompletedTask;
            }

            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, AndroidLauncher.FileProviderAuthority, new Java.IO.File(file));
            var install = new Intent(Intent.ActionView);
            install.SetDataAndType(uri, ApkMimeType);
            install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            context.StartActivity(install);
            _logger.LogInformation("Opened the installer for {File}", file);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open the installer");
            ErrorMessage = $"Could not open the installer: {ex.Message}";
        }
        finally
        {
            RaiseChanged();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _http.Dispose();
    }

    /// <summary>
    /// GitHub's releases API, or a developer override returning the same JSON: HELM_UPDATE_SOURCE (baked in with
    /// -p:HelmAndroidEnvFile) or Updates.SourceOverride in general.json, when it is an http(s) URL.
    /// </summary>
    private async Task<IReadOnlyList<GitHubRelease>> FetchReleasesAsync(CancellationToken ct)
    {
        var url = new[] { Environment.GetEnvironmentVariable("HELM_UPDATE_SOURCE"), _general.Current.Updates.SourceOverride }
            .FirstOrDefault(o => Uri.TryCreate(o, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http", StringComparison.Ordinal))
            ?? GitHubReleases.ApiUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private static async Task<bool> IsValidDownloadAsync(string file, ReleaseCandidate candidate, CancellationToken ct)
    {
        var info = new FileInfo(file);
        if (candidate.Asset.Size > 0 && info.Length != candidate.Asset.Size) return false;
        if (candidate.Sha256 is not { } expected) return true;
        await using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
        return hash == expected;
    }

    private string ApkPath(string version) => Path.Combine(_downloadDirectory, $"Helm-{version}.apk");

    /// <summary>APKs of the running version or older are leftovers of an installed update.</summary>
    private void DeleteStaleDownloads()
    {
        try
        {
            if (!Directory.Exists(_downloadDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(_downloadDirectory))
            {
                var name = Path.GetFileName(file);
                var version = name.StartsWith("Helm-", StringComparison.Ordinal) && name.EndsWith(".apk", StringComparison.Ordinal) ? name[5..^4] : null;
                if (version is null || UpdatePolicy.VersionFromTag(version) is null || !UpdatePolicy.IsNewer(version, CurrentVersion)) TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clean the update cache");
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "GitHub refused the request (rate limit). Try again in an hour.",
        HttpRequestException or TaskCanceledException => "Could not reach GitHub. Check your connection and try again.",
        _ => ex.Message,
    };

    private void RaiseChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
