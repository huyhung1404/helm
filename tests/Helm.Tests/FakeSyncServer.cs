using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>
/// In-memory reference implementation of the server side of docs/sync-protocol.md: compare-and-set on the record
/// version, one global seq, paged pulls. The Cloudflare Durable Object must behave exactly like this.
/// </summary>
internal sealed class FakeSyncServer
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Collection, string Id), RemoteRecord> _records = new();
    private readonly Dictionary<string, BlobEntry> _blobs = new(StringComparer.Ordinal);
    private long _seq;

    /// <summary>Server clock for blob timestamps (tests move it to exercise garbage collection).</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Bytes of blobs the account may store; exceeding it is a quota error, like the Worker.</summary>
    public long BlobQuota { get; set; } = long.MaxValue;

    public IReadOnlyDictionary<string, BlobEntry> Blobs
    {
        get { lock (_gate) return new Dictionary<string, BlobEntry>(_blobs); }
    }

    internal sealed class BlobEntry(string id, long size, int chunkCount, DateTimeOffset createdAt)
    {
        public string Id { get; } = id;
        public long Size { get; } = size;
        public int ChunkCount { get; } = chunkCount;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public bool Committed { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public Dictionary<int, byte[]> Chunks { get; } = [];

        public RemoteBlob ToRemote() => new(Id, Size, ChunkCount, Committed ? RemoteBlobState.Committed : RemoteBlobState.Pending,
            CreatedAt, DeletedAt, Chunks.Keys.Order().ToList());
    }

    public IReadOnlyList<RemoteRecord> Records
    {
        get { lock (_gate) return _records.Values.OrderBy(r => r.Seq).ToList(); }
    }

    public Transport Connect() => new(this);

    /// <summary>Simulates a malicious or buggy server moving a payload onto another record.</summary>
    public void SwapPayloads(string collection, string idA, string idB)
    {
        lock (_gate)
        {
            var a = _records[(collection, idA)];
            var b = _records[(collection, idB)];
            _records[(collection, idA)] = a with { Payload = b.Payload, Seq = ++_seq, Version = a.Version + 1 };
            _records[(collection, idB)] = b with { Payload = a.Payload, Seq = ++_seq, Version = b.Version + 1 };
        }
    }

    private IReadOnlyList<PushOutcome> Push(IReadOnlyList<PushItem> items)
    {
        lock (_gate)
        {
            var outcomes = new List<PushOutcome>();
            foreach (var item in items)
            {
                var key = (item.Collection, item.Id);
                _records.TryGetValue(key, out var current);
                if (item.BaseVersion != (current?.Version ?? 0))
                {
                    outcomes.Add(new PushOutcome(item.Collection, item.Id, false, 0, 0, current));
                    continue;
                }
                var stored = new RemoteRecord(item.Collection, item.Id, (current?.Version ?? 0) + 1, ++_seq, item.Deleted, item.Payload);
                _records[key] = stored;
                outcomes.Add(new PushOutcome(item.Collection, item.Id, true, stored.Version, stored.Seq, null));
            }
            return outcomes;
        }
    }

    private PullPage Pull(long since, int limit)
    {
        lock (_gate)
        {
            var newer = _records.Values.Where(r => r.Seq > since).OrderBy(r => r.Seq).ToList();
            var page = newer.Take(limit).ToList();
            return new PullPage(page, page.Count > 0 ? page[^1].Seq : since, newer.Count > page.Count);
        }
    }

    private RemoteBlob Reserve(string id, long size, int chunkCount)
    {
        lock (_gate)
        {
            if (_blobs.TryGetValue(id, out var existing))
            {
                if (existing.Size != size || existing.ChunkCount != chunkCount)
                    throw new SyncRequestException(System.Net.HttpStatusCode.Conflict, "blob_exists", "blob exists");
                return existing.ToRemote();
            }
            if (_blobs.Values.Sum(b => b.Size) + size > BlobQuota) throw new SyncQuotaException("Storage quota exceeded.");
            var blob = new BlobEntry(id, size, chunkCount, Now());
            _blobs[id] = blob;
            return blob.ToRemote();
        }
    }

    private void PutChunk(string id, int index, byte[] data)
    {
        lock (_gate)
        {
            var blob = _blobs.GetValueOrDefault(id) ?? throw new SyncRequestException(System.Net.HttpStatusCode.NotFound, "blob_not_found", "no blob");
            if (blob.Committed) throw new SyncRequestException(System.Net.HttpStatusCode.Conflict, "blob_committed", "committed");
            if (index < 0 || index >= blob.ChunkCount) throw new SyncRequestException(System.Net.HttpStatusCode.BadRequest, "invalid_index", "index");
            blob.Chunks[index] = data;
            if (blob.Chunks.Values.Sum(c => (long)c.Length) > blob.Size)
                throw new SyncRequestException(System.Net.HttpStatusCode.BadRequest, "chunk_exceeds_blob", "too big");
        }
    }

    private RemoteBlob Commit(string id)
    {
        lock (_gate)
        {
            var blob = _blobs.GetValueOrDefault(id) ?? throw new SyncRequestException(System.Net.HttpStatusCode.NotFound, "blob_not_found", "no blob");
            if (blob.Chunks.Count != blob.ChunkCount || blob.Chunks.Values.Sum(c => (long)c.Length) != blob.Size)
                throw new SyncRequestException(System.Net.HttpStatusCode.Conflict, "blob_incomplete", "incomplete");
            blob.Committed = true;
            return blob.ToRemote();
        }
    }

    internal sealed class Transport(FakeSyncServer server) : ISyncTransport, IBlobTransport
    {
        public bool IsConfigured => true;

        /// <summary>Counts chunk uploads; set <see cref="FailChunkUploadsAfter"/> to cut the connection mid-upload.</summary>
        public int ChunkUploads { get; private set; }

        public int? FailChunkUploadsAfter { get; set; }

        public Task<SyncLimits> GetLimitsAsync(CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            return Task.FromResult(new SyncLimits(4 * 1024 * 1024 + 4096, 65, 65L * (4 * 1024 * 1024 + 4096), 1024 * 1024));
        }

        public Task<RemoteBlob> ReserveAsync(string id, long size, int chunkCount, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            return Task.FromResult(server.Reserve(id, size, chunkCount));
        }

        public Task PutChunkAsync(string id, int index, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (Offline || ChunkUploads >= FailChunkUploadsAfter) throw new HttpRequestException("offline");
            ChunkUploads++;
            server.PutChunk(id, index, data.ToArray());
            return Task.CompletedTask;
        }

        public Task<RemoteBlob> CommitAsync(string id, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            return Task.FromResult(server.Commit(id));
        }

        public Task<RemoteBlob?> GetAsync(string id, CancellationToken ct)
        {
            lock (server._gate) return Task.FromResult(server._blobs.GetValueOrDefault(id)?.ToRemote());
        }

        public Task<RemoteBlobPage> ListAsync(string? after, int limit, CancellationToken ct)
        {
            lock (server._gate)
            {
                var all = server._blobs.Values.Where(b => after is null || string.CompareOrdinal(b.Id, after) > 0).OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
                var page = all.Take(limit).Select(b => b.ToRemote() with { Chunks = [] }).ToList();
                return Task.FromResult(new RemoteBlobPage(page, all.Count > page.Count, page.Count > 0 ? page[^1].Id : null));
            }
        }

        public Task<byte[]?> GetChunkAsync(string id, int index, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            lock (server._gate)
            {
                var blob = server._blobs.GetValueOrDefault(id);
                return Task.FromResult(blob is { Committed: true } && blob.Chunks.TryGetValue(index, out var data) ? data.ToArray() : null);
            }
        }

        public Task DeleteAsync(string id, CancellationToken ct)
        {
            lock (server._gate)
            {
                if (!server._blobs.TryGetValue(id, out var blob)) return Task.CompletedTask;
                if (blob.Committed) blob.DeletedAt ??= server.Now();
                else server._blobs.Remove(id);
            }
            return Task.CompletedTask;
        }

        public Task RestoreAsync(string id, CancellationToken ct)
        {
            lock (server._gate)
            {
                if (server._blobs.TryGetValue(id, out var blob)) blob.DeletedAt = null;
            }
            return Task.CompletedTask;
        }

        /// <summary>When set, pushes and pulls fail as if the network were down.</summary>
        public bool Offline { get; set; }

        /// <summary>Runs after the server stored a push but before the device sees the reply.</summary>
        public Action? AfterPushStored { get; set; }

        /// <summary>Runs when the device asks for a page, before the server answers.</summary>
        public Action? BeforePull { get; set; }

        public Task<IReadOnlyList<PushOutcome>> PushAsync(IReadOnlyList<PushItem> items, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            var outcomes = server.Push(items);
            AfterPushStored?.Invoke();
            return Task.FromResult(outcomes);
        }

        public Task<PullPage> PullAsync(long sinceSeq, int limit, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            BeforePull?.Invoke();
            return Task.FromResult(server.Pull(sinceSeq, limit));
        }
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
