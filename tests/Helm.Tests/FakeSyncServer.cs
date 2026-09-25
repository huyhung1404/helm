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
    private long _seq;

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

    internal sealed class Transport(FakeSyncServer server) : ISyncTransport
    {
        public bool IsConfigured => true;

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
