using System.Collections.Concurrent;
using Helm.Ai.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ai.Assistant;

/// <summary>
/// An agent CLI on this device (Codex, Claude Code, Grok) answering text-only jobs: the features' calls
/// (<see cref="AiTextService"/>), the phone's through Helm Sync (<see cref="AgentJobWorker"/>), a VPS service's. No tool of
/// Helm's runs for it. One chat per conversation, kept until it ends or sits idle.
/// </summary>
public sealed class LocalAgentRoute : IAgentRoute, IDisposable
{
    /// <summary>A conversation's chat goes after this long without a turn.</summary>
    internal static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(5);

    /// <summary>The system prompt of a job that brings none (and has no prompt of the user's on the Skill Hub).</summary>
    internal const string NeutralSystem = "You are a helpful assistant. Answer the request directly, in the language it is written in.";

    private readonly IAssistantChatFactory _agent;
    private readonly Func<string?> _unavailable;
    private readonly TimeProvider _time;
    private readonly IAiPrompts? _prompts;
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
    private readonly Timer _reaper;
    private readonly ILogger _logger;
    private readonly string _kind;
    private readonly Func<IReadOnlyList<string>> _models;

    /// <param name="agentName">The agent as the user knows it ("Codex", "Claude Code").</param>
    /// <param name="prompts">The user's own prompts (Skill Hub), for a job that brings none of its own.</param>
    /// <param name="kind">"codex", "claude-code" or "grok", for the phone's picker.</param>
    /// <param name="models">The models it runs, the one for a job whose model is not of its kind first; empty: its own only.</param>
    public LocalAgentRoute(string device, string agentName, IAssistantChatFactory agent, Func<string?> unavailable, TimeProvider? time = null,
        IAiPrompts? prompts = null, ILogger? logger = null, string kind = "", Func<IReadOnlyList<string>>? models = null)
    {
        _kind = kind;
        _models = models ?? (() => []);
        Device = device;
        AgentName = agentName;
        _agent = agent;
        _unavailable = unavailable;
        _time = time ?? TimeProvider.System;
        _prompts = prompts;
        _logger = logger ?? NullLogger.Instance;
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string Device { get; }

    /// <summary>The agent as the user named it; a rename takes effect at once, without stopping its conversations.</summary>
    public string AgentName { get; set; }

    public string? Unavailable() => _unavailable();

    /// <summary>What the phone's picker shows of it.</summary>
    public AiWorkerAgent Offer() => new() { Name = AgentName, Kind = _kind, Models = _models(), Problem = Unavailable() };

    public IReadOnlyList<AiAgentOffer> Offers => [new(Device, Offer())];

    /// <summary>
    /// The model it runs for a job: the job's when it is of its kind (a Claude model for Claude Code), else the first
    /// of its own; null for an agent that runs only its own (Codex).
    /// </summary>
    internal string? ModelFor(string requested)
    {
        var models = _models();
        if (models.Count == 0) return null;
        var prefix = _kind switch { "claude-code" => "claude-", "grok" => "grok-", _ => null };
        return models.Contains(requested) || (prefix is not null && requested.StartsWith(prefix, StringComparison.Ordinal)) ? requested : models[0];
    }

    public async Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
    {
        if (Unavailable() is { } why) throw new AiRouteException(why);
        var started = _time.GetTimestamp();
        var conversation = Open(job);
        await conversation.Gate.WaitAsync(ct);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(wait);
            AssistantStep step;
            try
            {
                conversation.Chat.AddUser(job.Text);
                step = await conversation.Chat.NextAsync(limit.Token);
                _logger.LogDebug("{Agent} answered in {Took:0} ms", AgentName, _time.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception ex) when (ex is OperationCanceledException or AssistantException or AssistantImageException or IOException or InvalidOperationException)
            {
                // Too long, cancelled by the asker, or the agent's process broke: the chat is not reused.
                _logger.LogDebug("{Agent} turn stopped ({Error})", AgentName, ex.GetType().Name);
                End(job.ConversationId);
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                throw new AiRouteException(ex is OperationCanceledException ? $"{Device} did not answer within {wait.TotalSeconds:0} s." : ex.Message);
            }
            conversation.LastTurn = _time.GetUtcNow();
            if (step.Refused)
            {
                End(job.ConversationId);
                throw new AiRouteException($"{job.Model} declined this request.");
            }
            // What it saved: the same tokens on the API with the model picked for the fallback.
            var u = step.Usage;
            return new AiJobResult(step.Text, [], [], AiPrices.Usd(job.Model, u.Input, u.Output, u.CacheRead, u.CacheWrite))
            {
                Agent = AgentName,
                Model = ModelFor(job.Model),
            };
        }
        finally
        {
            conversation.Gate.Release();
        }
    }

    public void End(string conversationId)
    {
        if (_conversations.TryRemove(conversationId, out var conversation)) (conversation.Chat as IDisposable)?.Dispose();
    }

    /// <summary>The conversation's chat, opened for this job when it has none.</summary>
    private Conversation Open(AiJob job) => _conversations.GetOrAdd(job.ConversationId, _ =>
    {
        var system = job.System ?? _prompts?.Get(job.Feature, NeutralSystem) ?? NeutralSystem;
        return new Conversation(_agent.Create("", ModelFor(job.Model) ?? job.Model, job.Effort, system, []), _time.GetUtcNow());
    });

    public void Dispose()
    {
        _reaper.Dispose();
        foreach (var id in _conversations.Keys.ToList()) End(id);
    }

    internal void Reap()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, conversation) in _conversations)
            if (now - conversation.LastTurn > IdleFor && conversation.Gate.CurrentCount == 1) End(id);
    }

    private sealed class Conversation(IAssistantChat chat, DateTimeOffset opened)
    {
        public IAssistantChat Chat { get; } = chat;
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Its last turn, or when it was opened.</summary>
        public DateTimeOffset LastTurn { get; set; } = opened;
    }
}

/// <summary>
/// This device's agents in the user's order (Codex, then Claude Code when it is set up): a conversation stays on the
/// agent that took its first turn; a new one goes to the first that can take it. The list can change under it (the
/// user reorders or turns one off) without stopping the conversations of the agents still in it.
/// </summary>
public sealed class AgentChain(IReadOnlyList<LocalAgentRoute> agents) : IAgentRoute
{
    private readonly ConcurrentDictionary<string, LocalAgentRoute> _taken = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<LocalAgentRoute> _agents = agents;

    /// <summary>The agents now, in order.</summary>
    public IReadOnlyList<LocalAgentRoute> Agents => _agents;

    public string Device => _agents.Count > 0 ? _agents[0].Device : Environment.MachineName;

    /// <summary>Takes the user's new list: a conversation whose agent left it goes to the first that can take it.</summary>
    public void Use(IReadOnlyList<LocalAgentRoute> agents)
    {
        _agents = agents;
        foreach (var (id, agent) in _taken)
            if (!agents.Contains(agent)) _taken.TryRemove(id, out _);
    }

    /// <summary>The agents that can take a job now, best first.</summary>
    public IReadOnlyList<string> Ready() => [.. _agents.Where(a => a.Unavailable() is null).Select(a => a.AgentName)];

    public IReadOnlyList<AiAgentOffer> Offers => [.. _agents.SelectMany(a => a.Offers)];

    public string? UnavailableFor(string agent) =>
        Named(agent) is { } named ? named.Unavailable() is { } why ? $"{agent}: {why}" : null : $"{agent} is not set up on {Device}.";

    private LocalAgentRoute? Named(string agent) =>
        _agents.FirstOrDefault(a => string.Equals(a.AgentName, agent, StringComparison.CurrentCultureIgnoreCase));

    public string? Unavailable()
    {
        var agents = _agents;
        return agents.Any(a => a.Unavailable() is null) ? null
            : agents.Count == 0 ? "No agent is set up on this PC."
            : string.Join(" ", agents.Select(a => $"{a.AgentName}: {a.Unavailable()}"));
    }

    public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
    {
        // The one the user picked, or none: never another agent in its place.
        if (job.Agent is { } picked)
        {
            if (UnavailableFor(picked) is { } why) throw new AiRouteException(why);
            var named = Named(picked)!;
            _taken[job.ConversationId] = named;
            return named.RunAsync(job, wait, ct);
        }
        var agents = _agents;
        var agent = _taken.TryGetValue(job.ConversationId, out var kept) && agents.Contains(kept) && kept.Unavailable() is null
            ? kept
            : agents.FirstOrDefault(a => a.Unavailable() is null) ?? throw new AiRouteException(Unavailable() ?? "No agent can take it.");
        _taken[job.ConversationId] = agent;
        return agent.RunAsync(job, wait, ct);
    }

    public void End(string conversationId)
    {
        if (_taken.TryRemove(conversationId, out var agent)) agent.End(conversationId);
    }
}
