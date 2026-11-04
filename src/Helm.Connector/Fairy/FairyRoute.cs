using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Core.Settings;

namespace Helm.Connector.Fairy;

/// <summary>
/// The connector's <see cref="IAgentRoute"/> for the phone's agent jobs: each one is handed to Fairy Core, the box's AI,
/// over the connector's socket (<c>helm/jobs/next</c>, answered with <c>helm/jobs/result</c>), and Undo runs here, on the
/// tools' own stores. <see cref="AgentJobWorker"/> speaks Helm Sync for it exactly as a PC does, so the phone sees one
/// more Helm PC. Fairy Core is "here" while it keeps asking for jobs (<see cref="SeenFor"/>).
/// </summary>
/// <remarks>
/// Nothing is kept across restarts: a job handed to a Core that went away, or waiting in a connector that stopped, ends
/// when its wait does, and the phone goes on to the next way, as with any PC that stopped mid-turn.
/// </remarks>
internal sealed class FairyRoute(Func<IEnumerable<IMcpToolProvider>> providers, string device, TimeProvider? time = null) : IAgentRoute
{
    /// <summary>Fairy Core counts as here this long after it last asked for a job (it asks at least every 25 s).</summary>
    public static readonly TimeSpan SeenFor = TimeSpan.FromSeconds(45);

    /// <summary>The longest a <c>helm/jobs/next</c> waits for a job.</summary>
    public static readonly TimeSpan LongestPoll = TimeSpan.FromSeconds(25);

    public const string Hello = "helm/jobs/hello", Next = "helm/jobs/next", Result = "helm/jobs/result", Undo = "helm/undo";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly BlockingCollection<Handoff> _queue = new(new ConcurrentQueue<Handoff>());
    private readonly ConcurrentDictionary<string, Handoff> _sent = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<AiWorkerAgent> _agents = [];
    private volatile bool _readsPhotos;
    private long _seenTicks = long.MinValue;

    /// <summary>Raised when Fairy Core comes, goes, or tells of other agents.</summary>
    public event Action? Changed;

    public string Device => device;

    public bool Connected => Interlocked.Read(ref _seenTicks) is var seen && seen != long.MinValue && _time.GetElapsedTime(seen) < SeenFor;

    public string? Unavailable() =>
        !Connected ? "Fairy Core is not connected."
        : _agents.Any(a => a.Problem is null) ? null
        : _agents.Count == 0 ? "Fairy Core has no agent set up."
        : string.Join(" ", _agents.Select(a => $"{a.Name}: {a.Problem}"));

    public string? UnavailableFor(string agent) =>
        !Connected ? "Fairy Core is not connected."
        : _agents.FirstOrDefault(a => string.Equals(a.Name, agent, StringComparison.CurrentCultureIgnoreCase)) is not { } named ? $"{agent} is not set up in Fairy Core."
        : named.Problem is { } why ? $"{agent}: {why}" : null;

    public IReadOnlyList<AiAgentOffer> Offers => Connected ? [.. _agents.Select(a => new AiAgentOffer(device, a))] : [];

    public bool ReadsPhotos => Connected && _readsPhotos;

    /// <summary>The agents that take a job now, best first (the worker record's list).</summary>
    public IReadOnlyList<string> Ready() => Connected ? [.. _agents.Where(a => a.Problem is null).Select(a => a.Name)] : [];

    /// <summary>The host methods only the connector's socket offers, for <see cref="McpServer.Methods"/>.</summary>
    public IEnumerable<KeyValuePair<string, Func<JsonObject?, CancellationToken, Task<JsonNode?>>>> Methods() =>
    [
        new(Hello, (p, _) => Task.FromResult<JsonNode?>(Greet(p))),
        new(Next, NextAsync),
        new(Result, (p, _) => Task.FromResult<JsonNode?>(Complete(p))),
        new(Undo, UndoMethodAsync),
    ];

    public async Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
    {
        if (Unavailable() is { } why) throw new AiRouteException(why);
        var handoff = Enqueue("run", job, job.ConversationId, answered: true);
        try
        {
            var answer = await handoff.Answer!.Task.WaitAsync(wait, ct);
            if (answer?["error"]?.GetValue<string>() is { } error)
                throw handoff.Delivered ? new AiRouteTakenException(error) : new AiRouteException(error);
            return answer?["result"]?.Deserialize<AiJobResult>(HelmJson.Options) ?? throw new AiRouteTakenException("Fairy Core answered nothing.");
        }
        catch (TimeoutException)
        {
            throw handoff.Delivered ? new AiRouteTakenException($"Fairy Core did not answer within {wait.TotalSeconds:0} s.")
                : new AiRouteException("Fairy Core did not take the job in time.");
        }
        finally
        {
            handoff.Over = true;
            _sent.TryRemove(handoff.Id, out _);
        }
    }

    /// <summary>A record the phone's Undo names was made here, by the connector's tools: it is taken back here.</summary>
    public async Task UndoAsync(AiJobAction action, CancellationToken ct)
    {
        using var result = JsonDocument.Parse(action.ResultJson);
        await UndoAsync(action.Tool, result.RootElement, ct);
    }

    public void End(string conversationId) => Enqueue("end", null, conversationId, answered: false);

    public void Warm(AiJob job)
    {
        if (Connected) Enqueue("warm", job, job.ConversationId, answered: false);
    }

    private Handoff Enqueue(string kind, AiJob? job, string? conversationId, bool answered)
    {
        var handoff = new Handoff(Guid.NewGuid().ToString("N"), kind, job, conversationId,
            answered ? new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously) : null);
        _queue.Add(handoff);
        return handoff;
    }

    private JsonNode Greet(JsonObject? hello)
    {
        _agents = hello?["agents"]?.Deserialize<List<AiWorkerAgent>>(HelmJson.Options) ?? [];
        _readsPhotos = hello?["readsPhotos"]?.GetValue<bool>() == true;
        Seen();
        Changed?.Invoke();
        return new JsonObject { ["device"] = device };
    }

    /// <summary>
    /// <c>helm/jobs/next {wait}</c>: the next job, or <c>{"job": null}</c> after at most <paramref name="parameters"/>'
    /// wait (25 s). Each call also says Fairy Core is here.
    /// </summary>
    private async Task<JsonNode?> NextAsync(JsonObject? parameters, CancellationToken ct)
    {
        var wasHere = Connected;
        Seen();
        if (!wasHere) Changed?.Invoke();
        var wait = parameters?["wait"]?.GetValue<double>() is { } seconds ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, LongestPoll.TotalSeconds)) : LongestPoll;
        try
        {
            var started = _time.GetTimestamp();
            while (true)
            {
                var left = wait - _time.GetElapsedTime(started);
                if (left <= TimeSpan.Zero) return new JsonObject { ["job"] = null };
                var taken = await Task.Run(() => _queue.TryTake(out var next, (int)Math.Ceiling(left.TotalMilliseconds), ct) ? next : null, ct);
                if (taken is null) return new JsonObject { ["job"] = null };
                // Its asker has gone on (the wait is over): Fairy Core never sees it.
                if (taken.Over || taken.Answer?.Task.IsCompleted == true) continue;
                taken.Delivered = true;
                if (taken.Answer is not null) _sent[taken.Id] = taken;
                return new JsonObject { ["job"] = Envelope(taken) };
            }
        }
        finally
        {
            Seen();
        }
    }

    private static JsonObject Envelope(Handoff handoff) => new()
    {
        ["id"] = handoff.Id,
        ["kind"] = handoff.Kind,
        ["conversationId"] = handoff.ConversationId,
        ["job"] = handoff.Job is { } job ? JsonSerializer.SerializeToNode(job, HelmJson.Options) : null,
        // Photos are never in a job's JSON (they travel as blobs between devices): here, beside it, for this turn only.
        ["images"] = handoff.Job is { Images.Count: > 0 } withImages
            ? new JsonArray([.. withImages.Images.Select(i => (JsonNode)new JsonObject { ["mediaType"] = i.MediaType, ["data"] = Convert.ToBase64String(i.Data) })])
            : null,
    };

    /// <summary><c>helm/jobs/result {id, result | error}</c>: the answer to a handed-off run.</summary>
    private JsonNode Complete(JsonObject? answer)
    {
        Seen();
        var id = answer?["id"]?.GetValue<string>() ?? throw new McpToolException("id is missing.");
        if (!_sent.TryRemove(id, out var handoff)) throw new McpToolException("No job waits for that answer (it ran out of time).");
        handoff.Answer!.TrySetResult(answer.DeepClone());
        return new JsonObject();
    }

    /// <summary><c>helm/undo {tool, result}</c>: takes back what one call of a tool did, given that call's result.</summary>
    private async Task<JsonNode?> UndoMethodAsync(JsonObject? parameters, CancellationToken ct)
    {
        var tool = parameters?["tool"]?.GetValue<string>() ?? throw new McpToolException("tool is missing.");
        using var result = JsonDocument.Parse(parameters["result"]?.ToJsonString() ?? "null");
        await UndoAsync(tool, result.RootElement, ct);
        return new JsonObject { ["undone"] = true };
    }

    private async Task UndoAsync(string tool, JsonElement result, CancellationToken ct)
    {
        var found = providers().SelectMany(p => p.Tools).FirstOrDefault(t => t.Name == tool);
        if (found?.Undo is not { } undo) throw new McpToolException($"{tool} cannot be undone here.");
        await undo(result.Clone(), ct);
    }

    private void Seen() => Interlocked.Exchange(ref _seenTicks, _time.GetTimestamp());

    private sealed class Handoff(string id, string kind, AiJob? job, string? conversationId, TaskCompletionSource<JsonNode?>? answer)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public AiJob? Job { get; } = job;
        public string? ConversationId { get; } = conversationId;
        public TaskCompletionSource<JsonNode?>? Answer { get; } = answer;
        private volatile bool _delivered, _over;

        public bool Delivered { get => _delivered; set => _delivered = value; }

        public bool Over { get => _over; set => _over = value; }
    }
}
