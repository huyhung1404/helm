using Helm.Ai;
using Helm.Ai.Assistant;

namespace Helm.Tests;

/// <summary>
/// Agent jobs: an agent on this PC answering a text job (R2), and the phone reaching it through Helm Sync (R3):
/// heartbeat, claim, result, the claim deadline, and the JSON call protocol.
/// </summary>
public sealed class AgentJobsTests
{
    private readonly MemorySynced<AiJobRecord> _jobs = new();
    private readonly MemorySynced<AiWorkerRecord> _workers = new();

    private static AiJob Job(string text = "phở 45k") =>
        new(AiFeatureIds.Default, "conv-1", text, [], [], "claude-haiku-5-5", "low");

    private static LocalAgentRoute Local(IAssistantChatFactory agent) => new("PC-HUNG", "Codex", agent, () => null);

    [Fact]
    public async Task On_the_PC_an_agent_answers_a_text_job_with_no_tool_and_what_it_saved()
    {
        var agent = new Scripted(Text("Xin chào.", 2000, 100));
        using var local = Local(agent);

        var result = await local.RunAsync(Job("hello") with { System = "You translate." }, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("Xin chào.", result.Text);
        Assert.Empty(result.Done);
        Assert.Empty(result.PhoneActions);
        Assert.Equal("Codex", result.Agent);
        Assert.NotNull(result.SavedUsd);
        Assert.Empty(agent.Tools);
        Assert.Equal("You translate.", agent.System);
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
        var pc = new FakeLocal(new AiJobResult("PC đã ghi.", [], [], 0.001m));
        using var worker = new AgentJobWorker(_jobs, _workers, pc, () => "pc-id", "PC-HUNG", () => ["Codex"]);
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");
        Assert.NotNull(phone.Unavailable());

        worker.Start();
        Assert.Null(phone.Unavailable());
        Assert.Equal("PC-HUNG", phone.Device);

        var result = await phone.RunAsync(Job(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("PC đã ghi.", result.Text);
        Assert.Equal("phở 45k", Assert.Single(pc.Jobs).Text);
        Assert.Empty(_jobs.All());
    }

    [Fact]
    public async Task A_turn_no_PC_claimed_in_time_is_cancelled_and_never_run_later()
    {
        var pc = new FakeLocal(new AiJobResult("late", [], [], null));
        _workers.Upsert("pc-id", new AiWorkerRecord { Device = "PC-HUNG", Agents = ["Codex"], SeenAt = DateTimeOffset.UtcNow });
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");

        await Assert.ThrowsAsync<AiRouteException>(() => phone.RunAsync(Job(), TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.Equal(AgentJobs.Cancelled, Assert.Single(_jobs.All()).Value.State);

        using var worker = new AgentJobWorker(_jobs, _workers, pc, () => "pc-id", "PC-HUNG", () => ["Codex"]);
        worker.Start();
        await Task.Delay(200);
        Assert.Empty(pc.Jobs);
    }

    [Fact]
    public async Task A_claim_that_comes_back_just_after_the_deadline_still_binds()
    {
        _workers.Upsert("pc-id", new AiWorkerRecord { Device = "PC-HUNG", Agents = ["Codex"], SeenAt = DateTimeOffset.UtcNow });
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");

        var run = phone.RunAsync(Job(), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await Task.Delay(700);
        // The PC claimed in time by its clock; its claim reaches the phone only now.
        var sent = Assert.Single(_jobs.All());
        Assert.Equal(AgentJobs.Queued, sent.Value.State);
        _jobs.SetRemote(sent.Id, sent.Value with { State = AgentJobs.Claimed });
        _jobs.SetRemote(sent.Id, sent.Value with { State = AgentJobs.Done, Result = new AiJobResult("Em ghi rồi.", [], [], null) });

        Assert.Equal("Em ghi rồi.", (await run).Text);
    }

    [Fact]
    public async Task A_turn_the_PC_took_and_then_failed_says_so_and_is_never_handed_on()
    {
        _workers.Upsert("pc-id", new AiWorkerRecord { Device = "PC-HUNG", Agents = ["Codex"], SeenAt = DateTimeOffset.UtcNow });
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");

        var run = phone.RunAsync(Job(), TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(100);
        var sent = Assert.Single(_jobs.All());
        _jobs.SetRemote(sent.Id, sent.Value with { State = AgentJobs.Claimed });
        _jobs.SetRemote(sent.Id, sent.Value with { State = AgentJobs.Failed, Error = "Codex stopped." });

        var failed = await Assert.ThrowsAsync<AiRouteTakenException>(() => run);
        Assert.Equal("Codex stopped.", failed.Message);
    }

    [Fact]
    public async Task Closing_the_card_withdraws_the_turn_and_a_PC_running_it_stops()
    {
        var pc = new Blocking();
        using var worker = new AgentJobWorker(_jobs, _workers, pc, () => "pc-id", "PC-HUNG", () => ["Codex"]);
        worker.Start();
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");
        using var close = new CancellationTokenSource();

        var run = phone.RunAsync(Job(), TimeSpan.FromSeconds(5), close.Token);
        await pc.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        close.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await pc.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_turn_not_taken_yet_is_withdrawn_when_the_card_closes()
    {
        _workers.Upsert("pc-id", new AiWorkerRecord { Device = "PC-HUNG", Agents = ["Codex"], SeenAt = DateTimeOffset.UtcNow });
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");
        using var close = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => phone.RunAsync(Job(), TimeSpan.FromSeconds(5), close.Token));

        Assert.Equal(AgentJobs.Cancelled, Assert.Single(_jobs.All()).Value.State);
    }

    [Fact]
    public void A_PC_long_unheard_of_is_offline_and_its_own_record_never_counts()
    {
        _workers.Upsert("pc-id", new AiWorkerRecord { Device = "PC-HUNG", Agents = ["Codex"], SeenAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10) });
        _workers.Upsert("phone-id", new AiWorkerRecord { Device = "Phone", Agents = ["Codex"], SeenAt = DateTimeOffset.UtcNow });

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

    private static AssistantStep Text(string text, long input = 100, long output = 10) => new(text, [], new AssistantUsage(input, output, 0, 0), false);

    private sealed class Scripted(params AssistantStep[] steps) : IAssistantChatFactory, IAssistantChat
    {
        private readonly Queue<AssistantStep> _steps = new(steps);
        private readonly TimeSpan _delay;

        public Scripted(TimeSpan delay) : this() => _delay = delay;

        public IReadOnlyList<AssistantTool> Tools { get; private set; } = [];
        public string System { get; private set; } = "";
        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            Tools = tools;
            System = system;
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

    /// <summary>A PC agent that runs until it is stopped.</summary>
    private sealed class Blocking : IAgentRoute
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Device => "PC-HUNG";
        public string? Unavailable() => null;

        public async Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                Stopped.TrySetResult();
            }
            return new AiJobResult("never", [], [], null);
        }

        public void End(string conversationId) { }
    }

    private sealed class FakeLocal(AiJobResult result) : IAgentRoute
    {
        public List<AiJob> Jobs { get; } = [];
        public string Device => "PC-HUNG";
        public string? Unavailable() => null;

        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            lock (Jobs) Jobs.Add(job);
            return Task.FromResult(result);
        }

        public void End(string conversationId) { }
    }
}
