using Helm.Core.Sync;

namespace Helm.Connector.Sync;

/// <summary>
/// The connector's sync transport: the HTTPS one, with a breaker on pushes. The box runs alone all day, so a bug that
/// writes in a loop must not hammer the server or flood the other devices: more than <see cref="MaxPushesPerMinute"/>
/// pushes within a minute, or <see cref="MaxPushesPerTenMinutes"/> within ten, trips it, and from then on the transport
/// says it is not configured, so the engine stops syncing (pulls, pushes, files, the live channel's runs) until the
/// process restarts.
/// </summary>
/// <remarks>
/// The box normally pushes about once a minute (a heartbeat, a tool call); a quick talk with Ask Helm through Fairy Core
/// adds about three per turn (the claim, the answer, the warm-up). A push is one request of up to 100 records, so a
/// minute carries 4,000 records: room for what piled up while it was offline. A runaway loop pushes far more than 40 a
/// minute (urgent records go out within 50 ms); one that writes every second (about 30 runs a minute) trips the
/// ten-minute limit within five.
/// </remarks>
internal sealed class PushBreaker(ISyncTransport inner, TimeProvider? time = null) : ISyncTransport, IBlobTransport, ISyncLiveTransport
{
    public const int MaxPushesPerMinute = 40, MaxPushesPerTenMinutes = 150;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1), TenMinutes = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Queue<DateTimeOffset> _pushes = new();
    private readonly object _gate = new();
    private string? _reason;

    /// <summary>Raised once, with the reason, when the breaker trips.</summary>
    public event Action<string>? Tripped;

    public string? TrippedReason => Volatile.Read(ref _reason);

    public DateTimeOffset? TrippedAt { get; private set; }

    public bool IsConfigured => _reason is null && inner.IsConfigured;

    public Task<IReadOnlyList<PushOutcome>> PushAsync(IReadOnlyList<PushItem> items, CancellationToken ct)
    {
        string? tripped = null;
        lock (_gate)
        {
            if (_reason is not null) throw new SyncPushBreakerException(_reason);
            var now = _time.GetUtcNow();
            while (_pushes.Count > 0 && now - _pushes.Peek() >= TenMinutes) _pushes.Dequeue();
            var lastMinute = _pushes.Count(p => now - p < Minute);
            if (lastMinute >= MaxPushesPerMinute || _pushes.Count >= MaxPushesPerTenMinutes)
            {
                var collections = string.Join(", ", items.Select(i => i.Collection).Distinct().Take(5));
                var limit = lastMinute >= MaxPushesPerMinute ? $"{MaxPushesPerMinute} pushes within a minute" : $"{MaxPushesPerTenMinutes} pushes within ten minutes";
                tripped = _reason = $"more than {limit} (the last one: {items.Count} records of {collections}); sync is stopped until helm-connector restarts";
                TrippedAt = now;
            }
            else _pushes.Enqueue(now);
        }
        if (tripped is null) return inner.PushAsync(items, ct);
        Tripped?.Invoke(tripped);
        throw new SyncPushBreakerException(tripped);
    }

    public Task<PullPage> PullAsync(long sinceSeq, int limit, SyncPullFilter filter, CancellationToken ct) =>
        _reason is { } reason ? throw new SyncPushBreakerException(reason) : inner.PullAsync(sinceSeq, limit, filter, ct);

    public Task<System.Net.WebSockets.WebSocket> ConnectLiveAsync(CancellationToken ct) =>
        _reason is not null || inner is not ISyncLiveTransport live
            ? throw new SyncLiveUnavailableException("The live channel is off.")
            : live.ConnectLiveAsync(ct);

    private IBlobTransport Blobs => _reason is { } reason ? throw new SyncPushBreakerException(reason)
        : inner as IBlobTransport ?? throw new InvalidOperationException("This transport has no files.");

    bool IBlobTransport.IsConfigured => IsConfigured && inner is IBlobTransport;

    public Task<SyncLimits> GetLimitsAsync(CancellationToken ct) => Blobs.GetLimitsAsync(ct);

    public Task<RemoteBlob> ReserveAsync(string id, long size, int chunkCount, CancellationToken ct) => Blobs.ReserveAsync(id, size, chunkCount, ct);

    public Task PutChunkAsync(string id, int index, ReadOnlyMemory<byte> data, CancellationToken ct) => Blobs.PutChunkAsync(id, index, data, ct);

    public Task PutChunksAsync(string id, IReadOnlyList<(int Index, long Length)> chunks, Func<int, CancellationToken, Task<byte[]>> read,
        CancellationToken ct) => Blobs.PutChunksAsync(id, chunks, read, ct);

    public Task<RemoteBlob> CommitAsync(string id, CancellationToken ct) => Blobs.CommitAsync(id, ct);

    public Task<RemoteBlob?> GetAsync(string id, CancellationToken ct) => Blobs.GetAsync(id, ct);

    public Task<RemoteBlobPage> ListAsync(string? after, int limit, CancellationToken ct) => Blobs.ListAsync(after, limit, ct);

    public Task<byte[]?> GetChunkAsync(string id, int index, CancellationToken ct) => Blobs.GetChunkAsync(id, index, ct);

    public Task DeleteAsync(string id, CancellationToken ct) => Blobs.DeleteAsync(id, ct);

    public Task RestoreAsync(string id, CancellationToken ct) => Blobs.RestoreAsync(id, ct);
}

/// <summary>The push breaker tripped: nothing goes to the server until the connector restarts.</summary>
internal sealed class SyncPushBreakerException(string message) : Exception(message);
