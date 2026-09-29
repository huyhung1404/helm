namespace Helm.Core.Sync;

/// <summary>
/// A record as the server stores it. The server never sees plaintext: <see cref="Payload"/> is an AES-GCM envelope
/// that also carries the schema version, edit time and device (see <see cref="SyncKeyring"/>).
/// </summary>
/// <param name="Version">Per-record version, incremented by the server on every accepted write (first write = 1).</param>
/// <param name="Seq">Global, strictly increasing sequence number assigned by the server on every accepted write.</param>
public sealed record RemoteRecord(string Collection, string Id, long Version, long Seq, bool Deleted, byte[] Payload);

/// <param name="BaseVersion">The server version this edit was made on (0 = the record is new to this device).</param>
public sealed record PushItem(string Collection, string Id, long BaseVersion, bool Deleted, byte[] Payload);

/// <summary>
/// Result for one <see cref="PushItem"/>. Accepted: the server stored it as <see cref="Version"/> at <see cref="Seq"/>.
/// Rejected: BaseVersion did not match; <see cref="Current"/> is what the server holds (null if it holds nothing).
/// </summary>
public sealed record PushOutcome(string Collection, string Id, bool Accepted, long Version, long Seq, RemoteRecord? Current);

/// <param name="NextSeq">
/// Cursor for the next pull: the highest seq returned while there is more, else the account's current seq (older
/// servers: the highest seq returned, or the requested one if none).
/// </param>
public sealed record PullPage(IReadOnlyList<RemoteRecord> Records, long NextSeq, bool HasMore);

/// <summary>
/// Which collections a pull returns (<c>?only=</c> / <c>?exclude=</c>). An entry ending in "." is a prefix: "vault."
/// is every vault.* collection. Servers from before filters ignore it, so the engine filters again itself.
/// </summary>
public sealed record SyncPullFilter(IReadOnlyList<string>? Only = null, IReadOnlyList<string>? Exclude = null)
{
    public static SyncPullFilter None { get; } = new();

    public bool IsEmpty => (Only is null || Only.Count == 0) && (Exclude is null || Exclude.Count == 0);

    public bool Matches(string collection)
    {
        if (Only is { Count: > 0 } only) return only.Any(e => Covers(e, collection));
        if (Exclude is { Count: > 0 } exclude) return !exclude.Any(e => Covers(e, collection));
        return true;
    }

    /// <summary>An entry ending in "." covers every collection starting with it; any other entry only itself.</summary>
    public static bool Covers(string entry, string collection) =>
        entry.EndsWith('.') ? collection.StartsWith(entry, StringComparison.Ordinal) : collection == entry;
}

/// <summary>
/// The server removed deletions newer than the pull cursor (410 resync_required, after <c>TOMBSTONE_DAYS</c>): this
/// device may have missed some, so it pulls everything again from 0 and drops what the server no longer has.
/// </summary>
public sealed class SyncResyncRequiredException(string message) : Exception(message);

/// <summary>
/// The live channel of a sync server (<c>GET /v1/sync/live</c>): a WebSocket that announces new seqs, so devices pull
/// at once instead of waiting for the next poll. Optional: a transport without it is polled only.
/// </summary>
public interface ISyncLiveTransport
{
    /// <exception cref="SyncAuthException">The token was refused: do not retry until it changes.</exception>
    /// <exception cref="SyncLiveUnavailableException">The server has no live channel (older server).</exception>
    Task<System.Net.WebSockets.WebSocket> ConnectLiveAsync(CancellationToken ct);
}

public sealed class SyncLiveUnavailableException(string message) : Exception(message);

/// <summary>
/// The wire contract with a sync server. Implementations only move bytes; conflict handling, crypto and storage
/// live in <see cref="SyncEngine"/>. Network failures surface as <see cref="HttpRequestException"/> or
/// <see cref="IOException"/>, which the engine reports as offline.
/// </summary>
public interface ISyncTransport
{
    /// <summary>False until a server and credentials are set up; the engine then does nothing.</summary>
    bool IsConfigured { get; }

    /// <summary>Compare-and-set per item: stored only when BaseVersion equals the server's current version.</summary>
    Task<IReadOnlyList<PushOutcome>> PushAsync(IReadOnlyList<PushItem> items, CancellationToken ct);

    /// <summary>Records written after <paramref name="sinceSeq"/>, in seq order, at most <paramref name="limit"/>.</summary>
    /// <exception cref="SyncResyncRequiredException">Deletions after <paramref name="sinceSeq"/> were cleaned up.</exception>
    Task<PullPage> PullAsync(long sinceSeq, int limit, SyncPullFilter filter, CancellationToken ct);
}

/// <summary>Used until sync is set up: data stays local and every sync is a no-op.</summary>
public sealed class NullSyncTransport : ISyncTransport
{
    public bool IsConfigured => false;

    public Task<IReadOnlyList<PushOutcome>> PushAsync(IReadOnlyList<PushItem> items, CancellationToken ct) =>
        throw new InvalidOperationException("Sync is not configured.");

    public Task<PullPage> PullAsync(long sinceSeq, int limit, SyncPullFilter filter, CancellationToken ct) =>
        throw new InvalidOperationException("Sync is not configured.");
}
