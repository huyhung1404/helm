using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>
/// Ask Helm answering sooner: the card's warm-up (a PC's agent started before the first words, sync's live channel
/// open), the API's answer streamed to the card as it comes (Claude and Grok), and how a phone waits on a PC's job.
/// </summary>
public sealed class AskHelmLatencyTests
{
    private const string ClaudeKey = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa";

    private readonly MemorySynced<AiJobRecord> _jobs = new();
    private readonly MemorySynced<AiWorkerRecord> _workers = new();
    private readonly WalletStore _wallet = new(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>());

    private static AiJob Job(string text = "phở 45k", string conversation = "conv-1", params string[] tools) =>
        new(AiFeatureIds.AskHelm, conversation, text, tools, [], "claude-haiku-5-5", "low");

    // ---- the warm-up ------------------------------------------------------------------------------------------

    [Fact]
    public void The_card_opening_warms_the_PC_and_opens_the_live_channel_until_the_conversation_ends()
    {
        var pc = new Warming();
        using var worker = new AgentJobWorker(_jobs, _workers, pc, () => "pc-id", "PC-HUNG", () => ["Claude Code"]);
        worker.Start();
        var live = new List<bool>();
        var states = new List<string>();
        _jobs.Changed += (_, e) => states.AddRange(e.Ids.Select(id => _jobs.Get(id)?.State ?? "gone"));
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id", new AgentJobSync(() => false, live.Add, () => { }), null, null);

        phone.Warm(Job(""));

        Assert.Equal("conv-1", Assert.Single(pc.Warmed).ConversationId);
        // Nobody waits for a warm-up: the PC writes no claim, the record just goes.
        Assert.DoesNotContain(AgentJobs.Claimed, states);
        Assert.Empty(_jobs.All());
        Assert.Equal([true], live);

        // Closed without a word: the PC's agent stops, and the channel goes back to how it was.
        phone.End("conv-1");
        Assert.Equal([true, false], live);
        Assert.True(SpinWait.SpinUntil(() => pc.Ended.Contains("conv-1"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_first_turn_goes_to_the_PC_that_got_ready_for_it()
    {
        _workers.Upsert("pc-a", new AiWorkerRecord { Device = "PC-A", Agents = ["Claude Code"], SeenAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30) });
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");
        phone.Warm(Job(""));
        // Another PC says it is there more recently: it would be the best, but PC-A has the agent ready.
        _workers.Upsert("pc-b", new AiWorkerRecord { Device = "PC-B", Agents = ["Claude Code"], SeenAt = DateTimeOffset.UtcNow });

        var run = phone.RunAsync(Job(), TimeSpan.FromSeconds(5), CancellationToken.None);
        var sent = Assert.Single(_jobs.All(), j => j.Value.Kind == AgentJobs.Run);
        Assert.Equal("pc-a", sent.Value.To);
        _jobs.SetRemote(sent.Id, sent.Value with { State = AgentJobs.Claimed });
        _jobs.SetRemote(sent.Id, sent.Value with { State = AgentJobs.Done, Result = new AiJobResult("Dạ.", [], [], null) });

        Assert.Equal("PC-A", (await run).Device);
    }

    [Fact]
    public async Task A_warmed_conversation_is_ready_for_its_first_turn_and_gets_the_turns_tools_then()
    {
        var agent = new WarmChat();
        using var local = Local(agent);

        local.Warm(Job("", tools: []));
        await agent.Warmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await local.RunAsync(Job("tháng này tiêu bao nhiêu", tools: "wallet_summary"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("Dạ.", result.Text);
        // One chat: the one started ahead, told about the tools the words needed.
        Assert.Equal(1, agent.Created);
        Assert.Equal(1, agent.Warms);
        Assert.Equal(["wallet_summary"], agent.Added);
    }

    [Fact]
    public async Task A_warm_up_no_turn_follows_ends_after_the_idle_time()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var agent = new WarmChat();
        using var local = Local(agent, time);

        local.Warm(Job(""));
        await agent.Warmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        local.Reap();
        Assert.False(agent.Disposed);

        time.Advance(LocalAgentRoute.IdleFor + TimeSpan.FromMinutes(1));
        // (Once the warm-up has let go of the conversation: one still starting is never reaped.)
        await Until(() =>
        {
            local.Reap();
            return agent.Disposed;
        });
    }

    [Fact]
    public async Task The_chain_warms_its_first_agent_and_the_conversation_stays_there()
    {
        var first = new WarmChat();
        var second = new WarmChat();
        using var a = Local(first);
        using var b = Local(second);
        var chain = new AgentChain([a, b]);

        chain.Warm(Job(""));
        await first.Warmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await chain.RunAsync(Job(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(1, first.Created);
        Assert.Equal(0, second.Created);
    }

    // ---- waiting on a PC --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_phone_waiting_on_a_PC_pulls_every_second_while_the_live_channel_is_down_and_seldom_while_it_is_up()
    {
        var time = new SteppedTime(DateTimeOffset.UtcNow);
        var live = false;
        var pulls = 0;
        _workers.Upsert("pc-id", new AiWorkerRecord { Device = "PC-HUNG", Agents = ["Codex"], SeenAt = time.GetUtcNow() });
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id",
            new AgentJobSync(() => live, _ => { }, () => Interlocked.Increment(ref pulls)), time, null);
        var armed = time.Created;

        var run = phone.RunAsync(Job(), TimeSpan.FromSeconds(30), CancellationToken.None);
        await Until(() => time.Created > armed);
        // Holding the live channel asks for one at once.
        Assert.Equal(1, Volatile.Read(ref pulls));

        await time.StepAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, Volatile.Read(ref pulls));
        await time.StepAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(3, Volatile.Read(ref pulls));

        // Up: every change is announced, so it only checks now and then.
        live = true;
        await time.StepAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(3, Volatile.Read(ref pulls));
        await time.StepAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, Volatile.Read(ref pulls));

        // A change sync brings wakes it at once, without any time passing.
        var sent = Assert.Single(_jobs.All());
        _jobs.SetRemote(sent.Id, sent.Value with { State = AgentJobs.Claimed });
        _jobs.SetRemote(sent.Id, sent.Value with { State = AgentJobs.Done, Result = new AiJobResult("Em ghi rồi.", [], [], null) });
        Assert.Equal("Em ghi rồi.", (await run.WaitAsync(TimeSpan.FromSeconds(5))).Text);
    }

    // ---- streaming --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claude_streams_the_answer_to_the_card_as_it_comes()
    {
        string? body = null;
        var http = new HttpClient(new Sse(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return ClaudeEvents(
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Dạ, "}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"tháng này Anh tiêu 3 triệu."}}""",
                """{"type":"content_block_stop","index":0}""",
                """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":12}}""");
        }));
        var chat = new ClaudeAssistantChatFactory(http).Create(ClaudeKey, "claude-haiku-5-5", "low", "Be Helm.", []);
        chat.AddUser("tháng này tiêu bao nhiêu");
        var seen = new List<string>();

        var step = await chat.NextAsync(seen.Add, CancellationToken.None);

        Assert.Equal(["Dạ, ", "Dạ, tháng này Anh tiêu 3 triệu."], seen);
        Assert.Equal("Dạ, tháng này Anh tiêu 3 triệu.", step.Text);
        Assert.Empty(step.Calls);
        Assert.Equal(120, step.Usage.Input);
        Assert.Equal(12, step.Usage.Output);
        Assert.True(JsonNode.Parse(body!)!["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Text_Claude_writes_before_a_tool_call_is_taken_back_and_the_call_comes_whole()
    {
        var http = new HttpClient(new Sse(_ => Task.FromResult(ClaudeEvents(
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Em xem nha."}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"wallet_summary","input":{}}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"month\":"}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"2026-11\"}"}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":30}}"""))));
        var chat = new ClaudeAssistantChatFactory(http).Create(ClaudeKey, "claude-haiku-5-5", "low", "Be Helm.", []);
        chat.AddUser("tháng này tiêu bao nhiêu");
        var seen = new List<string>();

        var step = await chat.NextAsync(seen.Add, CancellationToken.None);

        Assert.Equal(["Em xem nha.", ""], seen);
        var call = Assert.Single(step.Calls);
        Assert.Equal("wallet_summary", call.Name);
        Assert.Equal("2026-11", call.Input.GetProperty("month").GetString());
    }

    [Fact]
    public async Task The_card_hears_the_APIs_answer_while_it_comes_and_gets_the_same_reply_at_the_end()
    {
        var http = new HttpClient(new Sse(_ => Task.FromResult(ClaudeEvents(
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Dạ, "}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"chào Anh."}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}"""))));
        var keys = new AiKeyStore(new MemorySynced<AiKey>());
        keys.Add(AiProviders.Claude, "Helm", ClaudeKey, new AiKeyCheck(DateTimeOffset.UtcNow, AiKeyStatus.Works, "Works.", [new AiModel("claude-haiku-5-5", "Claude Haiku 5.5")]));
        var assistant = new HelmAssistant(keys, new MemorySettings<AiFeatureSettings>(), new AiUsageBook(new MemorySyncedLog<AiUsageEntry>()),
            [new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), _wallet), _wallet)], [new ClaudeAssistantChatFactory(http)], _ => true);
        var session = assistant.Start();
        var seen = new List<string>();
        session.OnPartialText = seen.Add;

        var reply = await session.SendAsync("chào em", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(["Dạ, ", "Dạ, chào Anh."], seen);
        Assert.Equal("Dạ, chào Anh.", reply.Text);
    }

    [Fact]
    public async Task Grok_streams_its_text_and_gathers_a_tool_call_sent_in_pieces()
    {
        string? body = null;
        var http = new HttpClient(new Sse(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return string.Concat(new[]
            {
                """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Dạ"}}]}""",
                """{"choices":[{"index":0,"delta":{"content":", em xem."}}]}""",
                """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"wallet_summary","arguments":"{\"mo"}}]}}]}""",
                """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"nth\":\"2026-11\"}"}}]}}]}""",
                """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":900,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":800}}}""",
            }.Select(d => $"data: {d}\n\n")) + "data: [DONE]\n\n";
        }));
        var chat = new GrokAssistantChatFactory(http).Create("xai-key", "grok-4.7", "low", "Be Helm.", []);
        chat.AddUser("tháng này tiêu bao nhiêu");
        var seen = new List<string>();

        var step = await chat.NextAsync(seen.Add, CancellationToken.None);

        Assert.Equal(["Dạ", "Dạ, em xem.", ""], seen);
        var call = Assert.Single(step.Calls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("2026-11", call.Input.GetProperty("month").GetString());
        Assert.Equal(100, step.Usage.Input);
        Assert.Equal(800, step.Usage.CacheRead);
        Assert.True(JsonNode.Parse(body!)!["stream"]!.GetValue<bool>());
    }

    // ---- doubles ----------------------------------------------------------------------------------------------

    private LocalAgentRoute Local(IAssistantChatFactory agent, TimeProvider? time = null) =>
        new("PC-HUNG", "Claude Code", agent, [new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), _wallet), _wallet)],
            _ => true, () => null, time);

    private static string ClaudeEvents(params string[] events)
    {
        var text = new StringBuilder();
        void Event(string data) => text.Append("event: ").Append(JsonNode.Parse(data)!["type"]!.GetValue<string>()).Append("\ndata: ").Append(data).Append("\n\n");
        Event("""{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":120,"output_tokens":1}}}""");
        foreach (var e in events) Event(e);
        Event("""{"type":"message_stop"}""");
        return text.ToString();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "it did not happen in time");
    }

    private sealed class Sse(Func<HttpRequestMessage, Task<string>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await answer(request), Encoding.UTF8, "text/event-stream") };
    }

    /// <summary>A PC agent that records warm-ups and ends.</summary>
    private sealed class Warming : IAgentRoute
    {
        public List<AiJob> Warmed { get; } = [];
        public List<string> Ended { get; } = [];
        public string Device => "PC-HUNG";
        public string? Unavailable() => null;
        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct) => Task.FromResult(new AiJobResult("Dạ.", [], [], null));
        public Task UndoAsync(AiJobAction action, CancellationToken ct) => Task.CompletedTask;

        public void End(string conversationId)
        {
            lock (Ended) Ended.Add(conversationId);
        }

        public void Warm(AiJob job) => Warmed.Add(job);
    }

    /// <summary>An agent chat that can be started ahead, and answers each turn at once.</summary>
    private sealed class WarmChat : IAssistantChatFactory, IAssistantChat, IDisposable
    {
        public TaskCompletionSource Warmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Created { get; private set; }
        public int Warms { get; private set; }
        public List<string> Added { get; } = [];
        public bool Disposed { get; private set; }
        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            Created++;
            return this;
        }

        public Task WarmAsync(CancellationToken ct)
        {
            Warms++;
            Warmed.TrySetResult();
            return Task.CompletedTask;
        }

        public void AddTools(IReadOnlyList<AssistantTool> tools) => Added.AddRange(tools.Select(t => t.Name));
        public void AddUser(string text) { }
        public Task<AssistantStep> NextAsync(CancellationToken ct) => Task.FromResult(new AssistantStep("Dạ.", [], new AssistantUsage(10, 2, 0, 0), false));
        public void AddResults(IReadOnlyList<ToolOutcome> results) { }
        public void Dispose() => Disposed = true;
    }
}

/// <summary>A clock whose timers (and so <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>) fire only when it is moved on.</summary>
internal sealed class SteppedTime(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Stepped> _timers = [];
    private DateTimeOffset _now = start;
    private int _created;

    /// <summary>How many timers were made so far: a waiting loop makes one each time it naps.</summary>
    public int Created => Volatile.Read(ref _created);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Stepped(this, () => callback(state));
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        Interlocked.Increment(ref _created);
        return timer;
    }

    /// <summary>Moves on and waits until the loop that napped has napped again.</summary>
    public async Task StepAsync(TimeSpan by)
    {
        var made = Created;
        List<Stepped> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => t.Due <= _now)];
            foreach (var t in due) t.Due = null;
        }
        foreach (var t in due) t.Fire();
        for (var i = 0; i < 500 && Created == made; i++) await Task.Delay(10);
        Assert.True(Created > made, "the wait did not go on");
    }

    private sealed class Stepped(SteppedTime owner, Action fire) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public void Fire() => fire();

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
