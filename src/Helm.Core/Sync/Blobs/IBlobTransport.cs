namespace Helm.Core.Sync;

public enum RemoteBlobState
{
    Pending,
    Committed,
    Purging,
}

/// <summary>A blob as the server knows it (sizes are of the encrypted chunks).</summary>
/// <param name="Chunks">Indexes the server has received; filled by <see cref="IBlobTransport.GetAsync"/> and reserve/commit.</param>
public sealed record RemoteBlob(
    string Id, long Size, int ChunkCount, RemoteBlobState State, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt,
    IReadOnlyList<int> Chunks);

public sealed record RemoteBlobPage(IReadOnlyList<RemoteBlob> Blobs, bool HasMore, string? Next);

/// <summary>The server's effective limits (<c>GET /v1/me</c>); configurable on the Worker, never hard-coded in Helm.</summary>
public sealed record SyncLimits(long MaxChunkBytes, int MaxChunksPerBlob, long MaxBlobBytes, long MaxPayloadBytes);

/// <summary>
/// The blob routes of the sync server (docs/sync-protocol.md, "Blobs"). Network failures surface as
/// <see cref="HttpRequestException"/>, a full quota as <see cref="SyncQuotaException"/>, a rejected token as
/// <see cref="SyncAuthException"/>.
/// </summary>
public interface IBlobTransport
{
    bool IsConfigured { get; }

    Task<SyncLimits> GetLimitsAsync(CancellationToken ct);

    /// <summary>Reserves quota for a blob; the same call again returns the current state (e.g. to resume).</summary>
    Task<RemoteBlob> ReserveAsync(string id, long size, int chunkCount, CancellationToken ct);

    /// <summary>Stores one encrypted chunk; sending the same index again replaces it.</summary>
    Task PutChunkAsync(string id, int index, ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <exception cref="SyncRequestException">Code blob_incomplete: chunks are missing.</exception>
    Task<RemoteBlob> CommitAsync(string id, CancellationToken ct);

    /// <returns>Null when the server does not know the blob (never uploaded, or purged).</returns>
    Task<RemoteBlob?> GetAsync(string id, CancellationToken ct);

    Task<RemoteBlobPage> ListAsync(string? after, int limit, CancellationToken ct);

    /// <returns>Null when the server has no such chunk.</returns>
    Task<byte[]?> GetChunkAsync(string id, int index, CancellationToken ct);

    /// <summary>Soft delete (kept 30 days, restorable); a pending blob is removed at once.</summary>
    Task DeleteAsync(string id, CancellationToken ct);

    Task RestoreAsync(string id, CancellationToken ct);
}

/// <summary>Until sync is set up: blobs stay on this device.</summary>
public sealed class NullBlobTransport : IBlobTransport
{
    public bool IsConfigured => false;

    public Task<SyncLimits> GetLimitsAsync(CancellationToken ct) => throw NotConfigured();
    public Task<RemoteBlob> ReserveAsync(string id, long size, int chunkCount, CancellationToken ct) => throw NotConfigured();
    public Task PutChunkAsync(string id, int index, ReadOnlyMemory<byte> data, CancellationToken ct) => throw NotConfigured();
    public Task<RemoteBlob> CommitAsync(string id, CancellationToken ct) => throw NotConfigured();
    public Task<RemoteBlob?> GetAsync(string id, CancellationToken ct) => throw NotConfigured();
    public Task<RemoteBlobPage> ListAsync(string? after, int limit, CancellationToken ct) => throw NotConfigured();
    public Task<byte[]?> GetChunkAsync(string id, int index, CancellationToken ct) => throw NotConfigured();
    public Task DeleteAsync(string id, CancellationToken ct) => throw NotConfigured();
    public Task RestoreAsync(string id, CancellationToken ct) => throw NotConfigured();

    private static InvalidOperationException NotConfigured() => new("Sync is not configured.");
}
