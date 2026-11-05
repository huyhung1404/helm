namespace Helm.Ai.Assistant;

/// <summary>
/// How ready one agent is for a conversation's next turn, as a route knows it now (<see cref="IAgentRoute.Readiness"/>).
/// </summary>
/// <param name="Agent">The agent as the user named it; "" for a route that does not say which agent runs it.</param>
/// <param name="Ready">It can take a job (installed, signed in, not at its plan's limit, its PC online).</param>
/// <param name="Warm">A process is up and idle for the turn: the conversation's own, or the spare a new one takes.</param>
/// <param name="Busy">It has the conversation, and its process is still on an earlier turn: this one would wait.</param>
/// <param name="Holds">It has the conversation (and what was said in it).</param>
/// <param name="FirstToken">How long its answers took to start lately (the first text the caller heard), if measured.</param>
public sealed record AgentReadiness(string Agent, string Device, bool Ready, bool Warm = false, bool Busy = false, bool Holds = false,
    TimeSpan? FirstToken = null);

/// <summary>Where a turn goes: an agent (<see cref="Chosen"/>), the API, or nowhere (<see cref="Problem"/>, said to the user).</summary>
public sealed record AgentPick(AgentReadiness? Chosen, bool Api, string? Problem)
{
    public static AgentPick Nowhere(string problem) => new(null, false, problem);
}

/// <summary>
/// Auto, cost first: a free agent on the user's own plan whenever one can start the turn now, the paid API only when
/// none can. Decided at once from what the routes know (ready, warm, busy, measured first-token times), never by trying
/// one and falling through to the next when it fails. An agent the user picked is the only one: never another in its place.
/// </summary>
public static class AgentPicker
{
    /// <param name="agents">Every agent the routes reach, in the user's order (this device's before another PC's).</param>
    /// <param name="picked">The agent the user picked by name, or null for Auto.</param>
    /// <param name="apiReady">The API can take the turn (a key that works, the policy allows it).</param>
    /// <param name="byLatency">Among agents equally ready, the one whose answers started soonest lately; else the user's order.</param>
    public static AgentPick Choose(IReadOnlyList<AgentReadiness> agents, string? picked, bool apiReady, bool byLatency = true)
    {
        if (picked is { Length: > 0 })
        {
            var named = agents.Where(a => string.Equals(a.Agent, picked, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (Best(named.Where(a => a.Ready).ToList(), byLatency) is { } mine) return new AgentPick(mine, false, null);
            return AgentPick.Nowhere(named.Count == 0 ? $"{picked} is not set up on any Helm PC." : $"{named[0].Agent} cannot take it now.");
        }
        if (Best([.. agents.Where(a => a.Ready && !a.Busy)], byLatency) is { } free) return new AgentPick(free, false, null);
        if (apiReady) return new AgentPick(null, true, null);
        // No API either: the agent that has the conversation takes it once its earlier turn is over.
        return agents.FirstOrDefault(a => a.Ready && a.Busy) is { } busy
            ? new AgentPick(busy, false, null)
            : AgentPick.Nowhere("No agent can take it now, and no API key is ready.");
    }

    /// <summary>
    /// The conversation's own agent first (it knows what was said), then one with a process up, then one that must start
    /// one; an agent still on an earlier turn of it last.
    /// </summary>
    private static AgentReadiness? Best(IReadOnlyList<AgentReadiness> agents, bool byLatency) =>
        agents.Select((a, i) => (Agent: a, Order: i))
            .OrderBy(x => x.Agent.Busy ? 3 : x.Agent.Holds ? 0 : x.Agent.Warm ? 1 : 2)
            .ThenBy(x => byLatency ? x.Agent.FirstToken ?? TimeSpan.MaxValue : TimeSpan.Zero)
            .ThenBy(x => x.Order)
            .Select(x => x.Agent)
            .FirstOrDefault();
}
