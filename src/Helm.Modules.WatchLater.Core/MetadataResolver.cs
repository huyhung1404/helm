using Helm.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.WatchLater;

/// <summary>
/// Fills in the title, channel, thumbnail and length of saved videos in the background, one video at a time, and
/// follows short links to the real video. Runs while the tool is on. A video that could not be looked up is tried
/// again the next time Helm starts (it may have been offline), at most once per run.
/// </summary>
public sealed class MetadataResolver : IDisposable
{
    private readonly WatchLaterStore _store;
    private readonly IReadOnlyList<IVideoMetadataSource> _sources;
    private readonly ILogger _logger;
    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MetadataResolver(WatchLaterStore store, IEnumerable<IVideoMetadataSource> sources, ILogger<MetadataResolver>? logger = null)
    {
        _store = store;
        _sources = sources.OrderBy(s => s.Order).ToList();
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _store.Changed += OnChanged;
    }

    /// <summary>Raised after a video's details were filled in (on a background thread).</summary>
    public event EventHandler<string>? Resolved;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => RunAsync(ct));
        }
        Wake();
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            _cts?.Cancel();
            _loop = null;
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Looks up one video now (for tests and "Refresh details"), whether or not it was tried before.</summary>
    public async Task ResolveAsync(string id, CancellationToken ct)
    {
        if (_store.Get(id) is not { } item) return;
        var found = VideoMetadata.Empty;
        foreach (var source in _sources)
        {
            var result = await source.FetchAsync(item, ct).ConfigureAwait(false);
            if (result is null) continue;
            found = found.Merge(result);
            if (found.IsComplete) break;
        }

        // A short link: switch to the real video (which may already be saved). A video link that leads to a Reel
        // (Facebook sends vertical videos there) becomes a Short.
        if (found.ResolvedUrl is { } resolvedUrl && VideoLink.Parse(resolvedUrl) is { NeedsResolve: false, ExternalId: not null } real
            && real.Source == item.Source
            && (item.Key.StartsWith("facebook:https://", StringComparison.Ordinal) || (real.Key == item.Key && real.Kind != item.Kind)))
        {
            id = _store.Resolve(id, real);
        }

        // Done once there is a title; else it is tried again on the next start.
        var done = !string.IsNullOrWhiteSpace(found.Title);
        _store.ApplyMetadata(id, found, done);
        Resolved?.Invoke(this, id);
    }

    private void OnChanged(object? sender, SyncedChangedEventArgs e) => Wake();

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _wake.WaitAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested && NextToResolve() is { } id)
            {
                try
                {
                    await ResolveAsync(id, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Looking up a saved video failed");
                }
            }
        }
    }

    /// <summary>The newest video that still needs its details and was not tried in this run.</summary>
    private string? NextToResolve()
    {
        var next = _store.All()
            .Where(i => !i.Value.MetadataDone && i.Value.Source != WatchSource.Other)
            .OrderByDescending(i => i.Value.AddedAt)
            .FirstOrDefault(i => !_tried.Contains(i.Id));
        if (next is null) return null;
        _tried.Add(next.Id);
        return next.Id;
    }

    public void Dispose()
    {
        _store.Changed -= OnChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _wake.Dispose();
    }
}
