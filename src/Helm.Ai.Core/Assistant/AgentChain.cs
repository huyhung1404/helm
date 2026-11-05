using System.Collections.Concurrent;

namespace Helm.Ai.Assistant;

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

    public bool ReadsPhotos => _agents.Any(a => a.ReadsPhotos);

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

    public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct) => RunAsync(job, wait, null, ct);

    public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, Action<string>? partial, CancellationToken ct)
    {
        // The one the user picked, or none: never another agent in its place.
        if (job.Agent is { } picked)
        {
            if (UnavailableFor(picked) is { } why) throw new AiRouteException(why);
            var named = Named(picked)!;
            if (job.Images.Count == 0) _taken[job.ConversationId] = named;
            return named.RunAsync(job, wait, partial, ct);
        }
        var agents = _agents;
        // A photo goes to the first agent that reads pictures (Grok's CLI takes none): each photo turn is a chat of its own.
        if (job.Images.Count > 0)
            return (agents.FirstOrDefault(a => a.ReadsPhotos) ?? throw new AiRouteException("No agent on this PC that reads photos can take it now."))
                .RunAsync(job, wait, partial, ct);
        var agent = _taken.TryGetValue(job.ConversationId, out var kept) && agents.Contains(kept) && kept.Unavailable() is null
            ? kept
            : agents.FirstOrDefault(a => a.Unavailable() is null) ?? throw new AiRouteException(Unavailable() ?? "No agent can take it.");
        _taken[job.ConversationId] = agent;
        return agent.RunAsync(job, wait, partial, ct);
    }

    /// <summary>The agent a new conversation would go to gets ready, and keeps the conversation (as its first turn would).</summary>
    public void Warm(AiJob job)
    {
        var ready = job.Agent is { } picked ? Named(picked) is { } named && named.Unavailable() is null ? named : null
            : _agents.FirstOrDefault(a => a.Unavailable() is null);
        if (_taken.ContainsKey(job.ConversationId) || ready is not { } agent) return;
        _taken[job.ConversationId] = agent;
        agent.Warm(job);
    }

    /// <summary>The tools are Helm's own on this PC: whichever agent asked for them, Helm takes the record back.</summary>
    public Task UndoAsync(AiJobAction action, CancellationToken ct) =>
        _agents is { Count: > 0 } agents ? agents[0].UndoAsync(action, ct) : throw new AiRouteException("No agent is set up on this PC to undo it.");

    public void End(string conversationId)
    {
        if (_taken.TryRemove(conversationId, out var agent)) agent.End(conversationId);
    }
}
