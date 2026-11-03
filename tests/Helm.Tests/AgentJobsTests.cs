using System.Text.Json;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>
/// Agent jobs: an agent on this PC running Helm's tools itself (R2), and the phone reaching it through Helm Sync (R3):
/// heartbeat, claim, result, the claim deadline, Undo where the record was made, and the JSON call protocol.
/// </summary>
public sealed class AgentJobsTests
{
    private readonly WalletStore _wallet = new(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>());
    private readonly MemorySynced<AiJobRecord> _jobs = new();
    private readonly MemorySynced<AiWorkerRecord> _workers = new();

    private static AiJob Job(string text = "phở 45k") =>
        new(AiFeatureIds.AskHelm, "conv-1", text, ["wallet_add_transaction", "wallet_summary", "tracker_start_task"], ["phone_set_alarm", "phone_clipboard"],
            "claude-haiku-5-5", "low");

    private LocalAgentRoute Local(Scripted agent) =>
        new("PC-HUNG", AiProviders.Claude, agent, [new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), _wallet), _wallet), new Starter()],
            _ => true, () => null);

    [Fact]
    public async Task On_the_PC_Helm_runs_the_tools_itself_and_hands_phone_ones_back()
    {
        var agent = new Scripted(
            Answer(("wallet_add_transaction", """{"amount":"45k","direction":"spend","description":"Phở","summary":"Chi 45.000đ · Phở"}"""),
                   ("phone_set_alarm", """{"time":"06:30","summary":"Báo thức 06:30"}""")),
            Text("Xong rồi Anh.", 2000, 100));
        using var local = Local(agent);

        var result = await local.RunAsync(Job(), TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("Xong rồi Anh.", result.Text);
        var done = Assert.Single(result.Done);
        Assert.Equal("Chi 45.000đ · Phở", done.Summary);
        Assert.Single(_wallet.Transactions());
        var phone = Assert.Single(result.PhoneActions);
        Assert.Equal("phone_set_alarm", phone.Tool);
        Assert.DoesNotContain("summary", phone.ArgumentsJson);
        Assert.NotNull(result.SavedUsd);
        // A PC cannot ask the phone for a yes mid-turn, nor read its clipboard: neither is offered.
        var offered = agent.Tools.Select(t => t.Name).ToList();
        Assert.DoesNotContain("tracker_start_task", offered);
        Assert.DoesNotContain("phone_clipboard", offered);
        Assert.Contains("phone_set_alarm", offered);

        await local.UndoAsync(done, CancellationToken.None);
        Assert.Empty(_wallet.Transactions());
    }

    [Fact]
    public async Task A_PC_agent_too_slow_gives_way()
    {
        using var local = Local(new Scripted(delay: TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAsync<AiRouteException>(() => local.RunAsync(Job(), TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task The_phone_reaches_the_PC_through_sync_and_gets_its_result()
    {
        var pc = new FakeLocal(new AiJobResult("PC đã ghi.", [new AiJobAction("wallet_add_transaction", "Chi 45.000đ", """{"id":"t1"}""")], [], 0.001m));
        using var worker = new AgentJobWorker(_jobs, _workers, pc, () => "pc-id", "PC-HUNG", [AiProviders.Claude]);
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");
        Assert.NotNull(phone.Unavailable());

        worker.Start();
        Assert.Null(phone.Unavailable());
        Assert.Equal("PC-HUNG", phone.Device);
        Assert.True(phone.Serves(AiProviders.Claude));
        Assert.False(phone.Serves(AiProviders.Grok));

        var result = await phone.RunAsync(Job(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("PC đã ghi.", result.Text);
        Assert.Equal("phở 45k", Assert.Single(pc.Jobs).Text);
        Assert.Empty(_jobs.All());

        await phone.UndoAsync(result.Done[0], CancellationToken.None);
        Assert.Equal("wallet_add_transaction", Assert.Single(pc.Undone).Tool);
    }

    [Fact]
    public async Task A_turn_no_PC_claimed_in_time_is_cancelled_and_never_run_later()
    {
        var pc = new FakeLocal(new AiJobResult("late", [], [], null));
        _workers.Upsert("pc-id", new AiWorkerRecord { Device = "PC-HUNG", Providers = ["anthropic"], SeenAt = DateTimeOffset.UtcNow });
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");

        await Assert.ThrowsAsync<AiRouteException>(() => phone.RunAsync(Job(), TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.Equal(AgentJobs.Cancelled, Assert.Single(_jobs.All()).Value.State);

        using var worker = new AgentJobWorker(_jobs, _workers, pc, () => "pc-id", "PC-HUNG", [AiProviders.Claude]);
        worker.Start();
        await Task.Delay(200);
        Assert.Empty(pc.Jobs);
    }

    [Fact]
    public void A_PC_long_unheard_of_is_offline_and_its_own_record_never_counts()
    {
        _workers.Upsert("pc-id", new AiWorkerRecord { Device = "PC-HUNG", Providers = ["anthropic"], SeenAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10) });
        _workers.Upsert("phone-id", new AiWorkerRecord { Device = "Phone", Providers = ["anthropic"], SeenAt = DateTimeOffset.UtcNow });

        Assert.NotNull(new SyncAgentRoute(_jobs, _workers, () => "phone-id").Unavailable());
    }

    [Theory]
    [InlineData("""{"calls":[{"name":"wallet_summary","input":{}}]}""", 1)]
    [InlineData("```json\n{\"calls\":[{\"name\":\"a\",\"input\":{\"x\":1}},{\"name\":\"b\"}]}\n```", 2)]
    [InlineData("Dạ, Anh đã chi 30.000đ.", 0)]
    [InlineData("""{"answer":"not calls"}""", 0)]
    public void Agent_answers_are_calls_or_text(string answer, int calls)
    {
        var seen = 0;
        var step = CliAgentProtocol.Parse(answer, new AssistantUsage(0, 0, 0, 0), ref seen);

        Assert.Equal(calls, step.Calls.Count);
        Assert.Equal(calls, seen);
        if (calls == 0) Assert.Equal(answer.Trim(), step.Text);
    }

    // ---- doubles ----------------------------------------------------------------------------------------------

    private static AssistantStep Answer(params (string Name, string Input)[] calls) =>
        new("", [.. calls.Select((c, i) => new ToolCall($"c{i}", c.Name, JsonDocument.Parse(c.Input).RootElement.Clone()))], new AssistantUsage(1000, 50, 0, 0), false);

    private static AssistantStep Text(string text, long input = 100, long output = 10) => new(text, [], new AssistantUsage(input, output, 0, 0), false);

    private sealed class Scripted(params AssistantStep[] steps) : IAssistantChatFactory, IAssistantChat
    {
        private readonly Queue<AssistantStep> _steps = new(steps);
        private readonly TimeSpan _delay;

        public Scripted(TimeSpan delay) : this() => _delay = delay;

        public IReadOnlyList<AssistantTool> Tools { get; private set; } = [];
        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            Tools = tools;
            return this;
        }

        public void AddUser(string text) { }

        public async Task<AssistantStep> NextAsync(CancellationToken ct)
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, ct);
            return _steps.Count > 0 ? _steps.Dequeue() : Text("");
        }

        public void AddResults(IReadOnlyList<ToolOutcome> results) { }
    }

    private sealed class FakeLocal(AiJobResult result) : IAgentRoute
    {
        public List<AiJob> Jobs { get; } = [];
        public List<AiJobAction> Undone { get; } = [];
        public string Device => "PC-HUNG";
        public bool Serves(AiProvider provider) => provider == AiProviders.Claude;
        public string? Unavailable() => null;

        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            lock (Jobs) Jobs.Add(job);
            return Task.FromResult(result);
        }

        public Task UndoAsync(AiJobAction action, CancellationToken ct)
        {
            Undone.Add(action);
            return Task.CompletedTask;
        }

        public void End(string conversationId) { }
    }

    /// <summary>A change with no Undo: on a PC it would need a yes, so it is not offered there.</summary>
    private sealed class Starter : IMcpToolProvider
    {
        public string? ModuleId => null;

        public IEnumerable<McpTool> Tools =>
            [new("tracker_start_task", "Start a task.", McpTool.NoArguments(), (_, _) => Task.FromResult<object?>("started"))];
    }
}
