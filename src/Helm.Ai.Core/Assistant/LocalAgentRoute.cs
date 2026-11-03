using System.Collections.Concurrent;
using System.Text.Json;
using Helm.Core.Mcp;

namespace Helm.Ai.Assistant;

/// <summary>
/// An agent CLI on this device (Codex, Claude Code) as Ask Helm's brain: Helm runs its own tools in-process for it (no
/// MCP, so no consent dialog on the PC for a request that came from the user's own phone), the agent only decides.
/// One chat per conversation, kept until it ends or sits idle; Undo runs here, where the records were made.
/// </summary>
public sealed class LocalAgentRoute : IAgentRoute, IDisposable
{
    /// <summary>A conversation's chat goes after this long without a turn.</summary>
    internal static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(5);

    private readonly string _agentName;
    private readonly IAssistantChatFactory _agent;
    private readonly IReadOnlyList<IMcpToolProvider> _providers;
    private readonly Func<string?, bool> _isToolOn;
    private readonly Func<string?> _unavailable;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
    private readonly Timer _reaper;

    /// <param name="agentName">The agent as the user knows it ("Codex", "Claude Code").</param>
    public LocalAgentRoute(string device, string agentName, IAssistantChatFactory agent, IEnumerable<IMcpToolProvider> providers,
        Func<string?, bool> isToolOn, Func<string?> unavailable, TimeProvider? time = null)
    {
        Device = device;
        _agentName = agentName;
        _agent = agent;
        _providers = [.. providers];
        _isToolOn = isToolOn;
        _unavailable = unavailable;
        _time = time ?? TimeProvider.System;
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string Device { get; }

    public string AgentName => _agentName;

    public string? Unavailable() => _unavailable();

    public async Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
    {
        if (Unavailable() is { } why) throw new AiRouteException(why);
        var conversation = _conversations.GetOrAdd(job.ConversationId, _ =>
        {
            var tools = Tools(job);
            return new Conversation(_agent.Create("", job.Model, job.Effort, job.System ?? HelmAssistant.Instructions, tools), tools);
        });
        await conversation.Gate.WaitAsync(ct);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(wait);
            long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
            // A later turn may need a group of tools the conversation did not: only those are added.
            var added = Tools(job).Where(t => conversation.Tools.All(h => h.Name != t.Name)).ToList();
            if (added.Count > 0)
            {
                conversation.Chat.AddTools(added);
                conversation.Tools.AddRange(added);
            }
            conversation.Chat.AddUser(job.Text);
            AssistantLoop.Outcome outcome;
            var records = new List<AiJobAction>();
            try
            {
                outcome = await AssistantLoop.RunAsync(conversation.Chat, conversation.Tools, _ => Task.FromResult(false),
                    u => { input += u.Input; output += u.Output; cacheRead += u.CacheRead; cacheWrite += u.CacheWrite; },
                    () => null, phoneHere: false, job.Model, limit.Token, records);
            }
            catch (Exception ex) when (ex is OperationCanceledException or AssistantException or IOException or InvalidOperationException)
            {
                // Too long, cancelled by the asker, or the agent's process broke: the chat is not reused. What it already
                // recorded is kept and goes back with Undo, never as a failure that someone might run again.
                End(job.ConversationId);
                var stopped = ex is OperationCanceledException
                    ? ct.IsCancellationRequested ? "Em dừng giữa chừng." : $"{Device} did not answer within {wait.TotalSeconds:0} s."
                    : ex.Message;
                if (records.Count > 0)
                    return new AiJobResult($"Em làm được một phần thì dừng ({stopped}).", [.. records], [],
                        AiPrices.Usd(job.Model, input, output, cacheRead, cacheWrite)) { Agent = _agentName };
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                throw new AiRouteException(stopped);
            }
            conversation.LastTurn = _time.GetUtcNow();
            if (outcome.Problem is { } problem && outcome.Records.Count == 0 && outcome.ForPhone.Count == 0)
            {
                End(job.ConversationId);
                throw new AiRouteException(problem);
            }
            // What it saved: the same tokens on the API with the model picked for the fallback.
            return new AiJobResult(outcome.Problem ?? outcome.Text, outcome.Records, outcome.ForPhone,
                AiPrices.Usd(job.Model, input, output, cacheRead, cacheWrite)) { Agent = _agentName };
        }
        finally
        {
            conversation.Gate.Release();
        }
    }

    public async Task UndoAsync(AiJobAction action, CancellationToken ct)
    {
        var tool = _providers.SelectMany(p => p.Tools).FirstOrDefault(t => t.Name == action.Tool);
        if (tool?.Undo is not { } undo) return;
        await undo(JsonDocument.Parse(action.ResultJson).RootElement.Clone(), ct);
    }

    public void End(string conversationId)
    {
        if (_conversations.TryRemove(conversationId, out var conversation)) (conversation.Chat as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        _reaper.Dispose();
        foreach (var id in _conversations.Keys.ToList()) End(id);
    }

    /// <summary>
    /// The job's tools that need no yes (a PC cannot ask the phone mid-turn), Helm's own ones run here, and the phone's
    /// handed back to it.
    /// </summary>
    internal IReadOnlyList<AssistantTool> Tools(AiJob job)
    {
        var mine = _providers.Where(p => _isToolOn(p.ModuleId)).SelectMany(p => p.Tools)
            .Where(t => job.Tools.Contains(t.Name) && t.Risk != McpRisk.Remote)
            .GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => g.First())
            .Select(t => new AssistantTool(t, HelmAssistant.ModeOf(t)))
            .Where(t => t.Mode != AssistantToolMode.Confirm);
        var phone = PhoneToolSpecs.All.Where(s => !s.ReadOnly && job.PhoneTools.Contains(s.Name))
            .Select(s => new AssistantTool(PhoneToolSpecs.HandedBack(s), AssistantToolMode.Auto));
        return [.. mine, .. phone];
    }

    private void Reap()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, conversation) in _conversations)
            if (now - conversation.LastTurn > IdleFor && conversation.Gate.CurrentCount == 1) End(id);
    }

    private sealed class Conversation(IAssistantChat chat, IReadOnlyList<AssistantTool> tools)
    {
        public IAssistantChat Chat { get; } = chat;
        public List<AssistantTool> Tools { get; } = [.. tools];
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastTurn { get; set; } = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// This device's agents in the user's order (Codex, then Claude Code when it is set up): a conversation stays on the
/// agent that took its first turn; a new one goes to the first that can take it.
/// </summary>
public sealed class AgentChain(IReadOnlyList<LocalAgentRoute> agents) : IAgentRoute
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LocalAgentRoute> _taken = new(StringComparer.Ordinal);

    public string Device => agents.Count > 0 ? agents[0].Device : Environment.MachineName;

    /// <summary>The agents that can take a job now, best first.</summary>
    public IReadOnlyList<string> Ready() => [.. agents.Where(a => a.Unavailable() is null).Select(a => a.AgentName)];

    public string? Unavailable() =>
        agents.Any(a => a.Unavailable() is null) ? null
        : agents.Count == 0 ? "No agent is set up on this PC."
        : string.Join(" ", agents.Select(a => $"{a.AgentName}: {a.Unavailable()}"));

    public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
    {
        var agent = _taken.TryGetValue(job.ConversationId, out var kept) && kept.Unavailable() is null
            ? kept
            : agents.FirstOrDefault(a => a.Unavailable() is null) ?? throw new AiRouteException(Unavailable() ?? "No agent can take it.");
        _taken[job.ConversationId] = agent;
        return agent.RunAsync(job, wait, ct);
    }

    /// <summary>The tools are Helm's own on this PC: whichever agent asked for them, Helm takes the record back.</summary>
    public Task UndoAsync(AiJobAction action, CancellationToken ct) =>
        agents.Count > 0 ? agents[0].UndoAsync(action, ct) : Task.CompletedTask;

    public void End(string conversationId)
    {
        if (_taken.TryRemove(conversationId, out var agent)) agent.End(conversationId);
    }
}
