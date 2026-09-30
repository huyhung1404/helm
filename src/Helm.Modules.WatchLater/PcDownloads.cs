using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.WatchLater;

/// <summary>
/// Downloads on Windows with yt-dlp: two at a time, the rest wait. Also picks up the downloads a phone asked for
/// (<see cref="WatchItem.DownloadRequest"/>) while the tool is on and "Download requests from your phone" is on, and
/// marks them downloaded on this PC so the phone shows it. A finished download shows a notification.
/// </summary>
public sealed class PcDownloads : IWatchDownloads, IDisposable
{
    private const int Parallel = 2;

    private readonly YtDlpTools _tools;
    private readonly WatchLaterStore _store;
    private readonly ISettingsStore<WatchLaterSettings> _settings;
    private readonly IDeviceInfo _device;
    // Resolved when a download finishes: the notifications (the tray) depend on the module list, which holds this.
    private readonly IServiceProvider _services;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger<PcDownloads> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, DownloadStatus> _status = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly List<(string Id, string Quality)> _queue = [];
    // Phone requests that failed in this run: not retried by themselves (Download retries).
    private readonly HashSet<string> _failedRequests = new(StringComparer.Ordinal);
    private bool _acceptRequests;

    public PcDownloads(YtDlpTools tools, WatchLaterStore store, ISettingsStoreFactory settings, IDeviceInfo device, IProcessLauncher launcher,
        ILogger<PcDownloads> logger, IServiceProvider services)
    {
        _tools = tools;
        _store = store;
        _settings = settings.Get<WatchLaterSettings>(WatchLaterIds.ModuleId);
        _device = device;
        _launcher = launcher;
        _logger = logger;
        _services = services;
        _store.Changed += (_, _) => TakeRequests();
        _settings.Changed += (_, _) => TakeRequests();
    }

    public bool DownloadsHere => true;

    public event EventHandler<string>? StatusChanged;

    /// <summary>The folder downloads go to.</summary>
    public string Folder => _settings.Current.DownloadFolder is { Length: > 0 } f ? f : DefaultFolder;

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Watch Later");

    /// <summary>The tool is on: phone requests are downloaded from now on.</summary>
    public void Start()
    {
        lock (_gate) _acceptRequests = true;
        TakeRequests();
    }

    /// <summary>The tool is off (or Helm exits): running downloads stop, nothing new starts.</summary>
    public void Stop()
    {
        List<string> ids;
        lock (_gate)
        {
            _acceptRequests = false;
            ids = _running.Keys.Concat(_queue.Select(q => q.Id)).ToList();
            foreach (var cts in _running.Values) cts.Cancel();
            _queue.Clear();
        }
        foreach (var id in ids) SetStatus(id, DownloadStatus.None);
    }

    public async Task<IReadOnlyList<QualityOption>> QualitiesAsync(WatchItem item, CancellationToken ct)
    {
        var probe = await _tools.ProbeAsync(item.Url, ct).ConfigureAwait(false);
        // The lookup found the details anyway: fill in what the card is missing.
        if (_store.Find(item.Key) is { } saved && (saved.Value.Title is null || saved.Value.DurationSeconds is null))
            _store.ApplyMetadata(saved.Id, probe.Metadata with { ThumbnailUrl = saved.Value.ThumbnailUrl ?? probe.Metadata.ThumbnailUrl }, done: probe.Metadata.Title is not null);
        return VideoQuality.ForFormats(probe.Formats, _tools.HasFfmpeg);
    }

    public bool NeedsFfmpeg => !_tools.HasFfmpeg;

    public Task InstallFfmpegAsync(IProgress<double> progress, CancellationToken ct) => _tools.InstallFfmpegAsync(progress, ct);

    public void Start(string id, string quality)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(id) || _queue.Any(q => q.Id == id)) return;
            _failedRequests.Remove(id);
            _queue.Add((id, quality));
        }
        SetStatus(id, new DownloadStatus(DownloadState.Queued));
        Pump();
    }

    public void Cancel(string id)
    {
        bool known;
        lock (_gate)
        {
            known = _queue.RemoveAll(q => q.Id == id) > 0;
            if (_running.TryGetValue(id, out var cts))
            {
                cts.Cancel();
                known = true;
            }
            // A phone's request that is not wanted: withdraw it so it is not picked up again.
            _failedRequests.Add(id);
        }
        if (_store.Get(id) is { DownloadRequest: not null }) _store.RequestDownload(id, null);
        if (known) SetStatus(id, DownloadStatus.None);
    }

    public DownloadStatus Status(string id)
    {
        lock (_gate) return _status.GetValueOrDefault(id) ?? DownloadStatus.None;
    }

    public string? FileFor(string id) =>
        _settings.Current.Downloads.TryGetValue(id, out var file) && File.Exists(file) ? file : null;

    public void Forget(string id)
    {
        Cancel(id);
        if (_settings.Current.Downloads.ContainsKey(id)) _settings.Update(s => s.Downloads.Remove(id));
        lock (_gate) _status.Remove(id);
    }

    /// <summary>Queues the downloads phones asked for that are not here yet.</summary>
    private void TakeRequests()
    {
        lock (_gate)
        {
            if (!_acceptRequests || !_settings.Current.DownloadRequests) return;
        }
        List<(string Id, string Quality)> wanted;
        try
        {
            wanted = _store.All()
                .Where(i => i.Value.DownloadRequest is not null && FileFor(i.Id) is null)
                .Select(i => (i.Id, i.Value.DownloadRequest!))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading download requests failed");
            return;
        }
        foreach (var (id, quality) in wanted)
        {
            bool skip;
            lock (_gate) skip = _failedRequests.Contains(id) || _running.ContainsKey(id) || _queue.Any(q => q.Id == id);
            if (!skip) Start(id, quality);
        }
        // Already downloaded here (e.g. before the phone asked): tell the phone.
        foreach (var item in _store.All().Where(i => i.Value.DownloadRequest is not null && FileFor(i.Id) is not null))
            _store.MarkDownloaded(item.Id, _device.DeviceName);
    }

    private void Pump()
    {
        while (true)
        {
            (string Id, string Quality) next;
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_running.Count >= Parallel || _queue.Count == 0) return;
                next = _queue[0];
                _queue.RemoveAt(0);
                cts = new CancellationTokenSource();
                _running[next.Id] = cts;
            }
            _ = Task.Run(() => RunAsync(next.Id, next.Quality, cts));
        }
    }

    private async Task RunAsync(string id, string quality, CancellationTokenSource cts)
    {
        var item = _store.Get(id);
        try
        {
            if (item is null) return;
            SetStatus(id, new DownloadStatus(DownloadState.Downloading, null, _tools.HasYtDlp ? "Starting the download…" : "Getting yt-dlp (first download only)…"));
            var lastReport = DateTime.MinValue;
            var file = await _tools.DownloadAsync(item.Url, quality, Folder, (fraction, text) =>
            {
                // yt-dlp prints many lines a second; the card needs a few.
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 250) return;
                lastReport = DateTime.UtcNow;
                var percent = fraction is { } f ? $" {f * 100:0} %" : "…";
                SetStatus(id, new DownloadStatus(DownloadState.Downloading, fraction, text + percent));
            }, cts.Token).ConfigureAwait(false);

            _settings.Update(s => s.Downloads[id] = file);
            if (_store.Get(id) is { DownloadRequest: not null }) _store.MarkDownloaded(id, _device.DeviceName);
            SetStatus(id, DownloadStatus.None);
            (_services.GetService(typeof(IUserNotifications)) as IUserNotifications)?.Show("Downloaded", WatchLaterFormat.Shorten(item.DisplayTitle, 80), () => _launcher.OpenFolder(Path.GetDirectoryName(file) ?? Folder));
        }
        catch (OperationCanceledException)
        {
            SetStatus(id, DownloadStatus.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download failed");
            lock (_gate) _failedRequests.Add(id);
            // YouTube keeps picture and sound apart; without ffmpeg only audio (and some Facebook videos) can be had.
            var text = !_tools.HasFfmpeg && quality != VideoQuality.Audio && ex.Message.Contains("format is not available", StringComparison.OrdinalIgnoreCase)
                ? "Install ffmpeg on this PC to download this video (Settings → Downloads)."
                : "Could not download: " + ex.Message;
            SetStatus(id, new DownloadStatus(DownloadState.Failed, null, text));
        }
        finally
        {
            lock (_gate) _running.Remove(id);
            cts.Dispose();
            Pump();
        }
    }

    private void SetStatus(string id, DownloadStatus status)
    {
        lock (_gate)
        {
            if (status.State == DownloadState.None) _status.Remove(id);
            else _status[id] = status;
        }
        StatusChanged?.Invoke(this, id);
    }

    public void Dispose() => Stop();
}
