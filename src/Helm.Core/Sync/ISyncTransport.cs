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

/// <param name="NextSeq">Cursor for the next pull (the highest seq returned, or the requested one if none).</param>
public sealed record PullPage(IReadOnlyList<RemoteRecord> Records, long NextSeq, bool HasMore);

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
    Task<PullPage> PullAsync(long sinceSeq, int limit, CancellationToken ct);
}

/// <summary>Used until sync is set up: data stays local and every sync is a no-op.</summary>
public sealed class NullSyncTransport : ISyncTransport
{
    public bool IsConfigured => false;

    public Task<IReadOnlyList<PushOutcome>> PushAsync(IReadOnlyList<PushItem> items, CancellationToken ct) =>
        throw new InvalidOperationException("Sync is not configured.");

    public Task<PullPage> PullAsync(long sinceSeq, int limit, CancellationToken ct) =>
        throw new InvalidOperationException("Sync is not configured.");
}
