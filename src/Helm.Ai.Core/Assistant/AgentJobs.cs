using System.Collections.Concurrent;
using Helm.Core.Mcp;
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

    public DateTimeOffset SeenAt { get; init; }
}

/// <summary>
/// One request between devices (collection <see cref="AgentJobs.JobsCollection"/>, urgent: it syncs at once): a turn
/// to run, an action to undo, or a conversation to end. The asker writes it queued; the worker claims it, then writes
/// the result. Nothing in it is readable by the sync server.
/// </summary>
public sealed record AiJobRecord
{
    public string Kind { get; init; } = AgentJobs.Run;

    /// <summary>The worker's device id.</summary>
    public required string To { get; init; }

    public required string From { get; init; }

    public AiJob? Job { get; init; }

    public AiJobAction? Action { get; init; }

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
    public const string Undo = "undo";
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
}

/// <summary>
/// An agent on another Helm device (the user's PC), reached through Helm Sync. A turn waits only <c>wait</c> for the
/// PC to claim it; once claimed it waits for the result and never falls back to the API, so nothing is done twice.
/// With two PCs online, a conversation stays on the PC that took its first turn (its chat is there), and Undo goes to
/// the PC that made the record (<see cref="AiJobAction.Worker"/>).
/// </summary>
public sealed class SyncAgentRoute(ISyncedCollection<AiJobRecord> jobs, ISyncedCollection<AiWorkerRecord> workers, Func<string> self,
    SyncEngine? engine = null, TimeProvider? time = null) : IAgentRoute
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The worker (device id) each conversation's turns go to, from the first turn a PC took.</summary>
    private readonly ConcurrentDictionary<string, string> _workers = new(StringComparer.Ordinal);

    private readonly LiveLease _live = engine is { } e ? new LiveLease(() => e.IsLive, e.SetLive, e.RequestSyncSoon) : LiveLease.None;

    public string Device => Best()?.Value.Device ?? "Helm PC";

    public string? Unavailable()
    {
        var online = Online().ToList();
        if (online.Count == 0) return "No Helm PC with an agent has been online in the last few minutes.";
        return online.All(w => w.Value.Problem is not null) ? online[0].Value.Problem : null;
    }

    public async Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
    {
        var worker = WorkerFor(job.ConversationId);
        var now = _time.GetUtcNow();
        var id = jobs.Add(new AiJobRecord
        {
            To = worker.Id, From = self(), Job = job, ConversationId = job.ConversationId,
            ClaimBy = now + wait, CreatedAt = now,
        });
        using var live = _live.Hold();
        Clean();
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
        return finished switch
        {
            { State: AgentJobs.Done, Result: { } result } => result with
            {
                Done = [.. result.Done.Select(a => a with { Worker = worker.Id })],
                Device = worker.Value.Device,
            },
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
    private SyncedItem<AiWorkerRecord> WorkerFor(string conversationId)
    {
        if (!_workers.TryGetValue(conversationId, out var kept))
            return Best() ?? throw new AiRouteException(Unavailable() ?? "No Helm PC online.");
        return Online().FirstOrDefault(w => w.Id == kept && w.Value.Problem is null)
            ?? throw new AiRouteException("The Helm PC that has this conversation cannot take it now.");
    }

    public async Task UndoAsync(AiJobAction action, CancellationToken ct)
    {
        // Where the record was made: it may not have synced anywhere else yet.
        var to = action.Worker ?? Best()?.Id ?? throw new AiRouteException("No Helm PC online to undo it.");
        var now = _time.GetUtcNow();
        var id = jobs.Add(new AiJobRecord { Kind = AgentJobs.Undo, To = to, From = self(), Action = action, ClaimBy = now + TimeSpan.FromSeconds(30), CreatedAt = now });
        using var live = _live.Hold();
        var done = await WaitForAsync(id, r => r.State is AgentJobs.Done or AgentJobs.Failed, TimeSpan.FromSeconds(30), ct);
        jobs.Delete(id);
        if (done?.State != AgentJobs.Done) throw new AiRouteException(done?.Error ?? "The PC did not undo it in time.");
    }

    public void End(string conversationId)
    {
        if (!_workers.TryRemove(conversationId, out var to)) return;
        var now = _time.GetUtcNow();
        jobs.Add(new AiJobRecord { Kind = AgentJobs.End, To = to, From = self(), ConversationId = conversationId, ClaimBy = now + TimeSpan.FromMinutes(5), CreatedAt = now });
    }

    private IEnumerable<SyncedItem<AiWorkerRecord>> Online()
    {
        var now = _time.GetUtcNow();
        var me = self();
        return workers.All().Where(w => w.Id != me && now - w.Value.SeenAt < AgentJobs.Online).OrderByDescending(w => w.Value.SeenAt);
    }

    private SyncedItem<AiWorkerRecord>? Best() => Online().FirstOrDefault(w => w.Value.Problem is null) ?? Online().FirstOrDefault();

    /// <summary>Polls the record (and asks sync to pull now and then, in case the live channel is down).</summary>
    private async Task<AiJobRecord?> WaitForAsync(string id, Func<AiJobRecord, bool> done, TimeSpan within, CancellationToken ct)
    {
        var until = _time.GetUtcNow() + within;
        var signal = new SemaphoreSlim(0);
        void OnChanged(object? _, SyncedChangedEventArgs e)
        {
            if (e.Ids.Contains(id)) signal.Release();
        }
        jobs.Changed += OnChanged;
        try
        {
            var lastPull = _time.GetUtcNow();
            while (true)
            {
                if (jobs.Get(id) is { } record && done(record)) return record;
                var left = until - _time.GetUtcNow();
                if (left <= TimeSpan.Zero) return null;
                if (_time.GetUtcNow() - lastPull > TimeSpan.FromSeconds(2))
                {
                    engine?.RequestSyncSoon();
                    lastPull = _time.GetUtcNow();
                }
                await signal.WaitAsync(left < TimeSpan.FromMilliseconds(500) ? left : TimeSpan.FromMilliseconds(500), ct);
            }
        }
        finally
        {
            jobs.Changed -= OnChanged;
        }
    }

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
/// overlap (a turn and an Undo): the channel goes back to how it was only when the last of them ends.
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
/// The PC side: says it is there every minute, takes the jobs sent to it, runs them on its own agent (Claude Code) with
/// Helm's tools, and writes back the results. Helm on the PC runs it while Helm Sync is set up.
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
                // The asker withdrew it (the card was closed): stop the run.
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
                    var result = await local.RunAsync(turn, AgentJobs.AfterClaim - TimeSpan.FromSeconds(10), stop.Token);
                    jobs.Upsert(id, job with { State = AgentJobs.Done, Result = result });
                    break;
                case AgentJobs.Undo when job.Action is { } action:
                    await local.UndoAsync(action, CancellationToken.None);
                    jobs.Upsert(id, job with { State = AgentJobs.Done });
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
            jobs.Upsert(id, job with { State = AgentJobs.Failed, Error = ex is AiRouteException or McpToolException ? ex.Message : $"It failed on {device}: {ex.Message}" });
        }
        finally
        {
            lock (_running) _running.Remove(id);
            lock (_taken) _taken.Remove(id);
        }
    }
}
