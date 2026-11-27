using System.Collections.Concurrent;
using Helm.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ai.Assistant;

/// <summary>
/// A Helm device that runs agent jobs for the others (a PC with Claude Code), as it last said so. Synced (collection
/// <see cref="AgentJobs.WorkersCollection"/>, one record per device id), end-to-end encrypted like everything.
/// </summary>
public sealed record AiWorkerRecord
{
    public required string Device { get; init; }

    /// <summary>Its agents that take jobs now, best first ("Codex", "Claude Code").</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];

    /// <summary>Why it takes no job now (Claude Code at its limit…); null when it does.</summary>
    public string? Problem { get; init; }

    /// <summary>Each of its agents, ready or not, with the models it runs (a Helm before 0.82 sends none: <see cref="Agents"/> only).</summary>
    public IReadOnlyList<AiWorkerAgent>? Offers { get; init; }

    public DateTimeOffset SeenAt { get; init; }
}

/// <summary>One agent on a Helm PC, as its heartbeat tells the phone's picker.</summary>
public sealed record AiWorkerAgent
{
    /// <summary>As the user named it on the PC ("Grok", "Claude Code").</summary>
    public required string Name { get; init; }

    /// <summary>"codex", "claude-code" or "grok".</summary>
    public string Kind { get; init; } = "";

    /// <summary>The models it runs, the one it runs for a job whose model is not one of these first; empty: only its own.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>Why it takes no job now (signed out, at its limit); null when it does.</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// One request between devices (collection <see cref="AgentJobs.JobsCollection"/>, urgent: it syncs at once): a text
/// job to run, or a conversation to end. The asker writes it queued; the worker claims it, then writes
/// the result. Nothing in it is readable by the sync server.
/// </summary>
public sealed record AiJobRecord
{
    public string Kind { get; init; } = AgentJobs.Run;

    /// <summary>The worker's device id.</summary>
    public required string To { get; init; }

    public required string From { get; init; }

    public AiJob? Job { get; init; }

    public string? ConversationId { get; init; }

    public string State { get; init; } = AgentJobs.Queued;

    /// <summary>A worker that sees it later leaves it: the asker has gone on without it.</summary>
    public DateTimeOffset ClaimBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public AiJobResult? Result { get; init; }

    public string? Error { get; init; }
}

public static class AgentJobs
{
    public const string JobsCollection = "ai.jobs";
    public const string WorkersCollection = "ai.workers";

    public const string Run = "run";
    public const string End = "end";

    public const string Queued = "queued";
    public const string Claimed = "claimed";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>A worker heard from within this is online.</summary>
    public static readonly TimeSpan Online = TimeSpan.FromMinutes(3);

    /// <summary>How often a worker says it is there.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(1);

    /// <summary>Once a worker has claimed a turn, how long the asker waits for its result (it never runs it twice elsewhere).</summary>
    public static readonly TimeSpan AfterClaim = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long past its deadline the asker still listens for a claim: the PC claims up to the deadline by its own
    /// clock, and the claim takes a moment (longer when the live channel is down) to come back. A claim seen in this
    /// time binds, so the turn never runs on the PC and on the API both.
    /// </summary>
    public static readonly TimeSpan ClaimGrace = TimeSpan.FromSeconds(4);

    /// <summary>Jobs older than this are cleared away.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(1);

    /// <summary>
    /// How often an asker waiting for a PC pulls by itself: seldom while the live channel announces every change (a
    /// safety net for a socket that died quietly), every second while it is down (connecting, or refused).
    /// </summary>
    public static readonly TimeSpan PullWhileLive = TimeSpan.FromSeconds(3), PullWhileDown = TimeSpan.FromSeconds(1);
}

/// <summary>What an asker needs of sync while it waits for a PC: whether the live channel is up, opening it, a pull soon.</summary>
internal sealed record AgentJobSync(Func<bool> IsLive, Action<bool> SetLive, Action PullSoon)
{
    public static AgentJobSync Of(SyncEngine engine) => new(() => engine.IsLive, engine.SetLive, engine.RequestSyncSoon);
}

/// <summary>
/// An agent on another Helm device (the user's PC), reached through Helm Sync. A turn waits only <c>wait</c> for the
/// PC to claim it; once claimed it waits for the result and never falls back to the API, so nothing is done twice.
/// With two PCs online, a conversation stays on the PC that took its first turn (its chat is there).
/// </summary>
public sealed class SyncAgentRoute : IAgentRoute
{
    private readonly ISyncedCollection<AiJobRecord> jobs;
    private readonly ISyncedCollection<AiWorkerRecord> workers;
    private readonly Func<string> self;
    private readonly AgentJobSync? _sync;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>The worker (device id) each conversation's turns go to, from the first turn a PC took.</summary>
    private readonly ConcurrentDictionary<string, string> _workers = new(StringComparer.Ordinal);

    private readonly LiveLease _live;

    public SyncAgentRoute(ISyncedCollection<AiJobRecord> jobs, ISyncedCollection<AiWorkerRecord> workers, Func<string> self,
        SyncEngine? engine = null, TimeProvider? time = null, ILogger? logger = null)
        : this(jobs, workers, self, engine is null ? null : AgentJobSync.Of(engine), time, logger)
    {
    }

    internal SyncAgentRoute(ISyncedCollection<AiJobRecord> jobs, ISyncedCollection<AiWorkerRecord> workers, Func<string> self,
        AgentJobSync? sync, TimeProvider? time, ILogger? logger)
    {
        this.jobs = jobs;
        this.workers = workers;
        this.self = self;
        _sync = sync;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _live = sync is { } s ? new LiveLease(s.IsLive, s.SetLive, s.PullSoon) : LiveLease.None;
    }

    public string Device => Best()?.Value.Device ?? "Helm PC";

    public string? Unavailable()
    {
        var online = Online().ToList();
        if (online.Count == 0) return "No Helm PC with an agent has been online in the last few minutes.";
        return online.All(w => w.Value.Problem is not null) ? online[0].Value.Problem : null;
    }

    public string? UnavailableFor(string agent)
    {
        var online = Online().ToList();
        if (online.Count == 0) return "No Helm PC with an agent has been online in the last few minutes.";
        if (online.Any(w => Has(w.Value, agent))) return null;
        return online.SelectMany(w => w.Value.Offers ?? []).FirstOrDefault(o => Same(o.Name, agent))?.Problem ?? $"No Helm PC online has {agent}.";
    }

    public IReadOnlyList<AiAgentOffer> Offers =>
    [
        .. Online().SelectMany(w => (w.Value.Offers ?? [.. w.Value.Agents.Select(a => new AiWorkerAgent { Name = a, Problem = w.Value.Problem })])
            .Select(o => new AiAgentOffer(w.Value.Device, o))),
    ];

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>The PC runs <paramref name="agent"/> now (a Helm before 0.82 names only its ready agents).</summary>
    private static bool Has(AiWorkerRecord worker, string agent) => worker.Offers is { } offers
        ? offers.Any(o => Same(o.Name, agent) && o.Problem is null)
        : worker.Problem is null && worker.Agents.Any(a => Same(a, agent));

    /// <summary>The PC takes a job for <paramref name="agent"/> (any of its agents when null).</summary>
    private static bool Fits(AiWorkerRecord worker, string? agent) => agent is null ? worker.Problem is null : Has(worker, agent);

    public async Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
    {
        var worker = WorkerFor(job.ConversationId, job.Agent);
        var now = _time.GetUtcNow();
        var id = jobs.Add(new AiJobRecord
        {
            To = worker.Id, From = self(), Job = job, ConversationId = job.ConversationId,
            ClaimBy = now + wait, CreatedAt = now,
        });
        using var live = _live.Hold();
        Clean();
        var askedAt = _time.GetTimestamp();
        AiJobRecord? claimed;
        try
        {
            claimed = await WaitForAsync(id, r => r.State != AgentJobs.Queued, wait + AgentJobs.ClaimGrace, ct);
        }
        catch (OperationCanceledException)
        {
            // The card was closed: the PC must not take it after all.
            Cancel(id);
            throw;
        }
        if (claimed is null || claimed.State == AgentJobs.Cancelled)
        {
            Cancel(id);
            throw new AiRouteException($"{worker.Value.Device} did not take it within {wait.TotalSeconds:0} s.");
        }
        // Its chat is on that PC now: the conversation's next turns, and its end, go there.
        _workers[job.ConversationId] = worker.Id;
        var claimedAt = _time.GetTimestamp();
        // Taken: from here on the turn is the PC's, whatever happens. It never falls back to the API.
        AiJobRecord? finished;
        try
        {
            finished = await WaitForAsync(id, r => r.State is AgentJobs.Done or AgentJobs.Failed, AgentJobs.AfterClaim, ct);
        }
        catch (OperationCanceledException)
        {
            // The card was closed: the PC stops the turn (what it already did stays).
            Cancel(id);
            throw;
        }
        jobs.Delete(id);
        // Where the time went (sync each way, or the PC's agent): the PC logs its own part.
        _logger.LogDebug("Agent turn on {Device}: claimed after {Claim:0} ms, its result came {Result:0} ms later (live channel {Live})",
            worker.Value.Device, _time.GetElapsedTime(askedAt, claimedAt).TotalMilliseconds, _time.GetElapsedTime(claimedAt).TotalMilliseconds,
            _sync?.IsLive() == true ? "up" : "down");
        return finished switch
        {
            { State: AgentJobs.Done, Result: { } result } => result with { Device = worker.Value.Device },
            { State: AgentJobs.Failed } failed => throw new AiRouteTakenException(failed.Error ?? $"{worker.Value.Device} could not do it."),
            _ => throw new AiRouteTakenException(
                $"{worker.Value.Device} took it but did not finish within {AgentJobs.AfterClaim.TotalSeconds:0} s: look on the PC whether it was done."),
        };
    }

    /// <summary>Withdraws a job not finished yet: a PC that has not taken it never will, one that has stops it.</summary>
    private void Cancel(string id)
    {
        if (jobs.Get(id) is { State: AgentJobs.Queued or AgentJobs.Claimed } open) jobs.Upsert(id, open with { State = AgentJobs.Cancelled });
    }

    /// <summary>
    /// The PC a turn goes to: the one that has the conversation, else the best online. A conversation whose PC is gone
    /// or busy now gives way (the API carries on with what was said), rather than start over on another PC.
    /// </summary>
    /// <param name="agent">The one agent the user picked; null for any.</param>
    private SyncedItem<AiWorkerRecord> WorkerFor(string conversationId, string? agent = null)
    {
        if (!_workers.TryGetValue(conversationId, out var kept))
        {
            if (agent is not null)
                return Online().FirstOrDefault(w => Fits(w.Value, agent)) ?? throw new AiRouteException(UnavailableFor(agent) ?? $"No Helm PC online has {agent}.");
            return Best() ?? throw new AiRouteException(Unavailable() ?? "No Helm PC online.");
        }
        return Online().FirstOrDefault(w => w.Id == kept && Fits(w.Value, agent))
            ?? throw new AiRouteException("The Helm PC that has this conversation cannot take it now.");
    }

    public void End(string conversationId)
    {
        // The PC that had its turns.
        if (!_workers.TryRemove(conversationId, out var worker)) return;
        var now = _time.GetUtcNow();
        jobs.Add(new AiJobRecord { Kind = AgentJobs.End, To = worker, From = self(), ConversationId = conversationId, ClaimBy = now + TimeSpan.FromMinutes(5), CreatedAt = now });
    }

    private IEnumerable<SyncedItem<AiWorkerRecord>> Online()
    {
        var now = _time.GetUtcNow();
        var me = self();
        return workers.All().Where(w => w.Id != me && now - w.Value.SeenAt < AgentJobs.Online).OrderByDescending(w => w.Value.SeenAt);
    }

    private SyncedItem<AiWorkerRecord>? Best() => Online().FirstOrDefault(w => w.Value.Problem is null) ?? Online().FirstOrDefault();

    /// <summary>
    /// Waits for the record: at once when sync brings a change of it, and asks sync to pull now and then by itself
    /// (<see cref="AgentJobs.PullWhileLive"/>, more often while the live channel is down).
    /// </summary>
    private async Task<AiJobRecord?> WaitForAsync(string id, Func<AiJobRecord, bool> done, TimeSpan within, CancellationToken ct)
    {
        var until = _time.GetUtcNow() + within;
        var changed = NewSignal();
        void OnChanged(object? _, SyncedChangedEventArgs e)
        {
            if (e.Ids.Contains(id)) Volatile.Read(ref changed).TrySetResult();
        }
        jobs.Changed += OnChanged;
        try
        {
            var lastPull = _time.GetUtcNow();
            while (true)
            {
                // Taken before the look: a change between the two wakes the wait below at once.
                var wake = Volatile.Read(ref changed).Task;
                if (jobs.Get(id) is { } record && done(record)) return record;
                var now = _time.GetUtcNow();
                if (until - now <= TimeSpan.Zero) return null;
                var every = _sync?.IsLive() == true ? AgentJobs.PullWhileLive : AgentJobs.PullWhileDown;
                if (now - lastPull >= every)
                {
                    _sync?.PullSoon();
                    lastPull = now;
                }
                var nap = Min(until - now, lastPull + every - now);
                using (var napping = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    await Task.WhenAny(wake, Task.Delay(nap, _time, napping.Token));
                    napping.Cancel();
                }
                ct.ThrowIfCancellationRequested();
                if (wake.IsCompleted) Volatile.Write(ref changed, NewSignal());
            }
        }
        finally
        {
            jobs.Changed -= OnChanged;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>The asker clears away its own old jobs.</summary>
    private void Clean()
    {
        var now = _time.GetUtcNow();
        var me = self();
        foreach (var old in jobs.All().Where(j => j.Value.From == me && now - j.Value.CreatedAt > AgentJobs.KeepFor).ToList()) jobs.Delete(old.Id);
    }
}

/// <summary>
/// A phone keeps sync's live channel open while it waits for a PC, so the answer arrives within a moment. Waits may
/// overlap (two features asking): the channel goes back to how it was only when the last of them ends.
/// </summary>
internal sealed class LiveLease(Func<bool> isLive, Action<bool> setLive, Action syncSoon)
{
    public static readonly LiveLease None = new(() => true, _ => { }, () => { });

    private readonly object _gate = new();
    private int _holders;
    private bool _was;

    public IDisposable Hold()
    {
        lock (_gate)
        {
            if (_holders++ == 0)
            {
                _was = isLive();
                if (!_was) setLive(true);
            }
        }
        syncSoon();
        return new Release(this);
    }

    private void Leave()
    {
        lock (_gate)
            if (--_holders == 0 && !_was) setLive(false);
    }

    private sealed class Release(LiveLease lease) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) lease.Leave();
        }
    }
}

/// <summary>
/// The PC side: says it is there every minute, takes the text jobs sent to it (the phone's features' AI calls), runs
/// them on its own agents, and writes back the results. Helm on the PC runs it while Helm Sync is set up.
/// </summary>
public sealed class AgentJobWorker(ISyncedCollection<AiJobRecord> jobs, ISyncedCollection<AiWorkerRecord> workers, IAgentRoute local,
    Func<string> self, string device, Func<IReadOnlyList<string>> agents, TimeProvider? time = null, ILogger? logger = null) : IDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private Timer? _heartbeat;

    public void Start()
    {
        if (_heartbeat is not null) return;
        jobs.Changed += OnChanged;
        Beat();
        _heartbeat = new Timer(_ => Beat(), null, AgentJobs.Heartbeat, AgentJobs.Heartbeat);
        foreach (var job in jobs.All()) Take(job.Id, job.Value);
    }

    /// <summary>Says it is there (or why it takes no job now).</summary>
    public void Beat()
    {
        try
        {
            workers.Upsert(self(), new AiWorkerRecord
            {
                Device = device,
                Agents = agents(),
                Problem = local.Unavailable(),
                Offers = [.. local.Offers.Select(o => o.Agent)],
                SeenAt = _time.GetUtcNow(),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the agent worker's heartbeat");
        }
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        jobs.Changed -= OnChanged;
        try
        {
            workers.Delete(self());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove the agent worker's record");
        }
    }

    private void OnChanged(object? sender, SyncedChangedEventArgs e)
    {
        foreach (var id in e.Ids)
        {
            if (jobs.Get(id) is not { } job) continue;
            if (job.State == AgentJobs.Cancelled)
            {
                // The asker withdrew it: stop the run.
                lock (_running)
                    if (_running.TryGetValue(id, out var running)) running.Cancel();
                continue;
            }
            Take(id, job);
        }
    }

    private void Take(string id, AiJobRecord job)
    {
        if (job.To != self() || job.State != AgentJobs.Queued) return;
        lock (_taken)
            if (!_taken.Add(id)) return;
        if (_time.GetUtcNow() > job.ClaimBy)
        {
            jobs.Upsert(id, job with { State = AgentJobs.Cancelled });
            return;
        }
        jobs.Upsert(id, job with { State = AgentJobs.Claimed });
        _ = Task.Run(() => DoAsync(id, job));
    }

    internal async Task DoAsync(string id, AiJobRecord job)
    {
        using var stop = new CancellationTokenSource();
        lock (_running) _running[id] = stop;
        try
        {
            switch (job.Kind)
            {
                case AgentJobs.Run when job.Job is { } turn:
                    var claimed = _time.GetTimestamp();
                    var result = await local.RunAsync(turn, AgentJobs.AfterClaim - TimeSpan.FromSeconds(10), stop.Token);
                    jobs.Upsert(id, job with { State = AgentJobs.Done, Result = result });
                    // From the phone's write to the claim (clocks may differ a little), then the agent's whole turn.
                    _logger.LogDebug("Agent turn claimed {Lag:0} ms after the asker wrote it; answered in {Took:0} ms",
                        (_time.GetUtcNow() - job.CreatedAt - _time.GetElapsedTime(claimed)).TotalMilliseconds, _time.GetElapsedTime(claimed).TotalMilliseconds);
                    break;
                case AgentJobs.End:
                    if (job.ConversationId is { } conversation) local.End(conversation);
                    jobs.Delete(id);
                    break;
                default:
                    jobs.Upsert(id, job with { State = AgentJobs.Failed, Error = $"Helm on {device} does not know job kind {job.Kind}." });
                    break;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            _logger.LogInformation("Agent job {Kind} withdrawn by the asker", job.Kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent job {Kind} failed", job.Kind);
            jobs.Upsert(id, job with { State = AgentJobs.Failed, Error = ex is AiRouteException ? ex.Message : $"It failed on {device}: {ex.Message}" });
        }
        finally
        {
            lock (_running) _running.Remove(id);
            lock (_taken) _taken.Remove(id);
        }
    }
}
