using System.Collections.Concurrent;
using System.Text.Json;
using Helm.Ai.Prompts;
using Helm.Core.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ai.Assistant;

/// <summary>
/// An agent CLI on this device (Codex, Claude Code) as Ask Helm's brain: Helm runs its own tools in-process for it (no
/// MCP, so no consent dialog on the PC for a request that came from the user's own phone), the agent only decides.
/// One chat per conversation, kept until it ends or sits idle, and (<see cref="KeepSpare"/>) one more started ahead
/// for the next conversation; Undo runs here, where the records were made.
/// </summary>
public sealed class LocalAgentRoute : IAgentRoute, IDisposable
{
    /// <summary>A conversation's chat goes after this long without a turn.</summary>
    internal static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(5);

    private readonly IAssistantChatFactory _agent;
    private readonly IReadOnlyList<IMcpToolProvider> _providers;
    private readonly Func<string?, bool> _isToolOn;
    private readonly Func<string?> _unavailable;
    private readonly TimeProvider _time;
    private readonly IAiPrompts? _prompts;
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
    private readonly Timer _reaper;
    private readonly object _opening = new();
    private Conversation? _spare;
    private long _firstTokenMs = -1;
    private bool _disposed;

    /// <summary>How long a warm-up may take (an agent's process starting) before it is given up.</summary>
    internal static readonly TimeSpan WarmFor = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private readonly string _kind;
    private readonly Func<IReadOnlyList<string>> _models;
    private readonly Func<McpTool, bool> _offers;

    /// <param name="agentName">The agent as the user knows it ("Codex", "Claude Code").</param>
    /// <param name="prompts">The user's own prompts (Skill Hub), for a job that brings none of its own.</param>
    /// <param name="kind">"codex", "claude-code" or "grok", for the phone's picker.</param>
    /// <param name="models">The models it runs, the one for a job whose model is not of its kind first; empty: its own only.</param>
    /// <param name="offers">The tools it may offer a job at all; null: Ask Helm's own (<see cref="HelmAssistant.Tools"/>).</param>
    public LocalAgentRoute(string device, string agentName, IAssistantChatFactory agent, IEnumerable<IMcpToolProvider> providers,
        Func<string?, bool> isToolOn, Func<string?> unavailable, TimeProvider? time = null, IAiPrompts? prompts = null, ILogger? logger = null,
        string kind = "", Func<IReadOnlyList<string>>? models = null, Func<McpTool, bool>? offers = null)
    {
        _offers = offers ?? (t => HelmAssistant.Tools.Contains(t.Name));
        _kind = kind;
        _models = models ?? (() => []);
        Device = device;
        AgentName = agentName;
        _agent = agent;
        _providers = [.. providers];
        _isToolOn = isToolOn;
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

    public bool ReadsPhotos => _agent.ReadsImages && Unavailable() is null;

    /// <summary>
    /// Keeps one chat more than the conversations have, its process started (<see cref="IAssistantChat.WarmAsync"/>), for
    /// the next conversation to take at once: a new question never waits for an agent to start, nor for one busy with
    /// another conversation. It is made like the last conversation opened (model, effort, instructions, tools) and taken
    /// by one that asks for the same; off by default (a Helm PC starts an agent when the card opens instead).
    /// </summary>
    public bool KeepSpare { get; set; }

    /// <summary>How long its answers took to start lately (the first text the caller heard), or null before one did.</summary>
    public TimeSpan? FirstToken => Interlocked.Read(ref _firstTokenMs) is var ms and >= 0 ? TimeSpan.FromMilliseconds(ms) : null;

    /// <summary>How ready it is for the conversation's next turn (<see cref="AgentPicker"/>).</summary>
    public AgentReadiness ReadinessFor(string conversationId)
    {
        var ready = Unavailable() is null;
        if (_conversations.TryGetValue(conversationId, out var conversation))
            return new AgentReadiness(AgentName, Device, ready, conversation.Ready && !conversation.Running, conversation.Running, true, FirstToken);
        var warm = Volatile.Read(ref _spare) is { Ready: true } spare && spare.Gate.CurrentCount == 1;
        return new AgentReadiness(AgentName, Device, ready, warm, FirstToken: FirstToken);
    }

    public IReadOnlyList<AgentReadiness> Readiness(string conversationId) => [ReadinessFor(conversationId)];

    /// <summary>What the phone's picker shows of it.</summary>
    public AiWorkerAgent Offer() => new() { Name = AgentName, Kind = _kind, Models = _models(), Problem = Unavailable(), ReadsPhotos = _agent.ReadsImages };

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

    public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct) => RunAsync(job, wait, null, ct);

    /// <summary>The turn, the answer's text so far told to <paramref name="partial"/> as the agent writes it.</summary>
    public async Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, Action<string>? partial, CancellationToken ct)
    {
        if (Unavailable() is { } why) throw new AiRouteException(why);
        if (job.Images.Count > 0 && !_agent.ReadsImages) throw new AiRouteException($"{AgentName} cannot read photos.");
        var timings = new TurnTimings();
        var conversation = Open(job, out var fresh);
        // A warm-up still starting the agent holds the gate: the turn waits for it rather than start a second one.
        await conversation.Gate.WaitAsync(ct);
        timings.Mark(fresh && !conversation.Ready ? "queue (cold start)" : conversation.Turns == 0 ? "queue (warmed)" : "queue");
        conversation.Running = true;
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
            AssistantLoop.Outcome outcome;
            var records = new List<AiJobAction>();
            try
            {
                AddUser(conversation.Chat, job);
                outcome = await AssistantLoop.RunAsync(conversation.Chat, conversation.Tools, _ => Task.FromResult(false),
                    u => { input += u.Input; output += u.Output; cacheRead += u.CacheRead; cacheWrite += u.CacheWrite; },
                    () => null, phoneHere: false, job.Model, limit.Token, records, Timed(partial), timings);
                conversation.Turns++;
                conversation.Ready = true;
                _logger.LogDebug("{Agent} turn: {Timings}", AgentName, timings);
            }
            catch (Exception ex) when (ex is OperationCanceledException or AssistantException or AssistantImageException or IOException or InvalidOperationException)
            {
                // Too long, cancelled by the asker, or the agent's process broke: the chat is not reused. What it already
                // recorded is kept and goes back with Undo, never as a failure that someone might run again.
                _logger.LogDebug("{Agent} turn stopped ({Error}): {Timings}", AgentName, ex.GetType().Name, timings);
                End(job.ConversationId);
                var stopped = ex is OperationCanceledException
                    ? ct.IsCancellationRequested ? "Em dừng giữa chừng." : $"{Device} did not answer within {wait.TotalSeconds:0} s."
                    : ex.Message;
                if (records.Count > 0)
                    return new AiJobResult($"Em làm được một phần thì dừng ({stopped}).", [.. records], [],
                        AiPrices.Usd(job.Model, input, output, cacheRead, cacheWrite)) { Agent = AgentName, Model = ModelFor(job.Model) };
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
                AiPrices.Usd(job.Model, input, output, cacheRead, cacheWrite)) { Agent = AgentName, Model = ModelFor(job.Model) };
        }
        finally
        {
            conversation.Running = false;
            conversation.Gate.Release();
            // A photo is never kept after its turn: the chat (and the agent's process) that saw it goes.
            if (job.Images.Count > 0) End(job.ConversationId);
        }
    }

    /// <summary>The job's words, with its photos when it has some (the words go with the first).</summary>
    private static void AddUser(IAssistantChat chat, AiJob job)
    {
        if (job.Images.Count == 0)
        {
            chat.AddUser(job.Text);
            return;
        }
        for (var i = 0; i < job.Images.Count; i++) chat.AddUser(i == 0 ? job.Text : "", job.Images[i]);
    }

    public async Task UndoAsync(AiJobAction action, CancellationToken ct)
    {
        var tool = _providers.SelectMany(p => p.Tools).FirstOrDefault(t => t.Name == action.Tool);
        if (tool?.Undo is not { } undo) throw new McpToolException($"Helm on {Device} cannot undo {action.Tool}.");
        await undo(JsonDocument.Parse(action.ResultJson).RootElement.Clone(), ct);
    }

    /// <summary>
    /// <paramref name="partial"/> (which may be null: the time is measured anyway) and the time the first text of the
    /// turn took, kept as <see cref="FirstToken"/> (weighted to the latest turns).
    /// </summary>
    private Action<string> Timed(Action<string>? partial)
    {
        var started = _time.GetTimestamp();
        var heard = false;
        return text =>
        {
            if (!heard && text.Length > 0)
            {
                heard = true;
                var ms = (long)_time.GetElapsedTime(started).TotalMilliseconds;
                var before = Interlocked.Read(ref _firstTokenMs);
                Interlocked.Exchange(ref _firstTokenMs, before < 0 ? ms : (before * 2 + ms) / 3);
            }
            partial?.Invoke(text);
        };
    }

    public void End(string conversationId)
    {
        if (_conversations.TryRemove(conversationId, out var conversation)) Drop(conversation);
    }

    /// <summary>Stops a chat (and the agent's process) no conversation will use.</summary>
    private static void Drop(Conversation conversation)
    {
        conversation.Dropped = true;
        (conversation.Chat as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Opens the conversation's chat now and lets the agent get ready (<see cref="IAssistantChat.WarmAsync"/>: its
    /// process starts) off the caller's thread. The first turn then finds it ready; one that never comes ends with
    /// <see cref="End"/> or after <see cref="IdleFor"/>.
    /// </summary>
    public void Warm(AiJob job)
    {
        if (Unavailable() is not null) return;
        var conversation = Open(job, out var fresh);
        if (fresh && !conversation.Ready) _ = Task.Run(() => WarmAsync(conversation));
    }

    private async Task WarmAsync(Conversation conversation)
    {
        await conversation.Gate.WaitAsync();
        var started = _time.GetTimestamp();
        bool Kept() => !conversation.Dropped;
        try
        {
            if (!Kept()) return;
            using var limit = new CancellationTokenSource(WarmFor);
            await conversation.Chat.WarmAsync(limit.Token);
            // Ended while its process started: that process must not outlive it.
            if (!Kept()) (conversation.Chat as IDisposable)?.Dispose();
            else
            {
                conversation.Ready = true;
                _logger.LogDebug("{Agent} ready for a conversation in {Took:0} ms", AgentName, _time.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (Exception ex) when (ex is AssistantException or IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // The first turn starts it again, as without a warm-up.
            _logger.LogDebug("{Agent} could not get ready ahead of the turn: {Error}", AgentName, ex.Message);
        }
        finally
        {
            conversation.Gate.Release();
        }
    }

    /// <summary>
    /// The conversation's chat, opened for this job when it has none (<paramref name="fresh"/>): the spare when it fits,
    /// and then another spare is made ready for the next one.
    /// </summary>
    private Conversation Open(AiJob job, out bool fresh)
    {
        Conversation conversation;
        lock (_opening)
        {
            fresh = !_conversations.TryGetValue(job.ConversationId, out var kept);
            if (kept is not null) return kept;
            var setup = SetupFor(job);
            conversation = TakeSpare(setup) ?? Make(setup);
            _conversations[job.ConversationId] = conversation;
        }
        if (KeepSpare) Refill(job);
        return conversation;
    }

    /// <summary>What a conversation's chat is made with: the model it runs, the effort, the instructions and the tools.</summary>
    private Setup SetupFor(AiJob job) => new(ModelFor(job.Model) ?? job.Model, job.Effort,
        job.System ?? _prompts?.Get(job.Feature, HelmAssistant.Instructions) ?? HelmAssistant.Instructions, Tools(job));

    private Conversation Make(Setup setup) =>
        new(_agent.Create("", setup.Model, setup.Effort, setup.System, setup.Tools), setup, _time.GetUtcNow());

    /// <summary>The spare, for a conversation made the same way (it gets the tools it lacks at its turn); one that does not fit goes.</summary>
    private Conversation? TakeSpare(Setup setup)
    {
        var spare = _spare;
        if (spare is null) return null;
        _spare = null;
        if (spare.Setup.Fits(setup))
        {
            spare.LastTurn = _time.GetUtcNow();
            return spare;
        }
        Drop(spare);
        return null;
    }

    /// <summary>Makes a spare like <paramref name="job"/>'s conversation and starts its agent, unless one fits already.</summary>
    private void Refill(AiJob job)
    {
        if (Unavailable() is not null) return;
        Conversation spare;
        lock (_opening)
        {
            var setup = SetupFor(job);
            if (_disposed || _spare is { } kept && kept.Setup.Fits(setup)) return;
            if (_spare is { } old) Drop(old);
            _spare = spare = Make(setup);
        }
        _ = Task.Run(() => WarmAsync(spare));
    }

    public void Dispose()
    {
        _reaper.Dispose();
        lock (_opening)
        {
            _disposed = true;
            if (_spare is { } spare) Drop(spare);
            _spare = null;
        }
        foreach (var id in _conversations.Keys.ToList()) End(id);
    }

    /// <summary>
    /// The job's tools that need no yes (a PC cannot ask the phone mid-turn), Helm's own ones run here, and the phone's
    /// handed back to it. Only Ask Helm's own tools (<see cref="HelmAssistant.Tools"/>), whatever names the job brings:
    /// a device in the sync group cannot ask for Slack, SSH or any tool Ask Helm never offers.
    /// </summary>
    internal IReadOnlyList<AssistantTool> Tools(AiJob job)
    {
        var mine = _providers.Where(p => _isToolOn(p.ModuleId)).SelectMany(p => p.Tools)
            .Where(t => job.Tools.Contains(t.Name) && _offers(t) && t.Risk != McpRisk.Remote)
            .GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => g.First())
            .Select(t => new AssistantTool(t, HelmAssistant.ModeOf(t)))
            .Where(t => t.Mode != AssistantToolMode.Confirm);
        var phone = PhoneToolSpecs.All.Where(s => !s.ReadOnly && job.PhoneTools.Contains(s.Name))
            .Select(s => new AssistantTool(PhoneToolSpecs.HandedBack(s), AssistantToolMode.Auto));
        return [.. mine, .. phone];
    }

    internal void Reap()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, conversation) in _conversations)
            if (now - conversation.LastTurn > IdleFor && conversation.Gate.CurrentCount == 1) End(id);
    }

    /// <summary>What a chat was made with (<see cref="SetupFor"/>).</summary>
    private sealed record Setup(string Model, string Effort, string System, IReadOnlyList<AssistantTool> Tools)
    {
        /// <summary>A chat made so can serve one asked for <paramref name="wanted"/>: the same, with no tool it does not ask for.</summary>
        public bool Fits(Setup wanted) =>
            Model == wanted.Model && Effort == wanted.Effort && System == wanted.System && Tools.All(t => wanted.Tools.Any(w => w.Name == t.Name));
    }

    private sealed class Conversation(IAssistantChat chat, Setup setup, DateTimeOffset opened)
    {
        public IAssistantChat Chat { get; } = chat;
        public Setup Setup { get; } = setup;
        public List<AssistantTool> Tools { get; } = [.. setup.Tools];
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Its agent got ready (a warm-up or a turn went through): the next turn starts no process.</summary>
        public volatile bool Ready;

        /// <summary>A turn is on.</summary>
        public volatile bool Running;

        /// <summary>Ended or given up: a warm-up that finishes after must not keep its process.</summary>
        public volatile bool Dropped;

        /// <summary>Its last turn, or when it was opened (a warm-up that no turn followed goes after the same idle time).</summary>
        public DateTimeOffset LastTurn { get; set; } = opened;

        public int Turns { get; set; }
    }
}
