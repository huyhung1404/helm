using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>
/// The card's model sheet (2026-11-04): Auto, one agent on a PC on one of its models, or one API model; the effort
/// slider's levels. A model picked is the one that answers: a Claude pick never runs on Grok again, and a PC agent
/// picked by name never hands the turn to another agent or to the API.
/// </summary>
public sealed class AskHelmPickerTests
{
    private const string ClaudeKey = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa";

    private readonly MemorySynced<AiKey> _keys = new();
    private readonly MemorySyncedLog<AiUsageEntry> _usage = new();
    private readonly MemorySettings<AiFeatureSettings> _features = new();
    private readonly MemorySynced<AiJobRecord> _jobs = new();
    private readonly MemorySynced<AiWorkerRecord> _workers = new();
    private readonly WalletStore _wallet = new(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>());

    private static AiJob Job(string? agent = null, string model = "claude-sonnet-5-5") =>
        new(AiFeatureIds.AskHelm, "conv-1", "phở 45k", [], [], model, "high") { Agent = agent };

    private static LocalAgentRoute Agent(string name, string kind, Recorder chat, Func<string?>? problem = null, params string[] models) =>
        new("PC-HUNG", name, chat, [], _ => true, problem ?? (() => null), kind: kind, models: () => models);

    [Fact]
    public async Task A_Grok_agent_asked_for_a_Claude_model_runs_its_own_and_says_which()
    {
        var grok = new Recorder();
        using var agent = Agent("Grok", "grok", grok, models: ["grok-4.7-build-fast", "grok-4.7"]);

        var result = await agent.RunAsync(Job(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("grok-4.7-build-fast", grok.Model);
        Assert.Equal("grok-4.7-build-fast", result.Model);
        Assert.Equal("Grok", result.Agent);
    }

    [Fact]
    public async Task The_agent_picked_by_name_takes_the_turn_and_no_other_does()
    {
        var grok = new Recorder();
        var claude = new Recorder();
        using var first = Agent("Grok", "grok", grok, models: ["grok-4.7-build-fast"]);
        using var second = Agent("Claude Code", "claude-code", claude, models: ["claude-haiku-5-5", "claude-opus-5-5"]);
        var chain = new AgentChain([first, second]);

        var result = await chain.RunAsync(Job("claude code", "claude-opus-5-5"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("Claude Code", result.Agent);
        Assert.Equal("claude-opus-5-5", claude.Model);
        Assert.Null(grok.Model);
    }

    [Fact]
    public async Task An_agent_picked_by_name_that_cannot_take_it_fails_rather_than_another_agent()
    {
        var grok = new Recorder();
        using var first = Agent("Grok", "grok", grok, models: ["grok-4.7-build-fast"]);
        using var second = Agent("Claude Code", "claude-code", new Recorder(), () => "signed out", "claude-haiku-5-5");
        var chain = new AgentChain([first, second]);

        var failed = await Assert.ThrowsAsync<AiRouteException>(() => chain.RunAsync(Job("Claude Code"), TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Contains("signed out", failed.Message);
        Assert.Null(grok.Model);
        Assert.Contains("not set up", chain.UnavailableFor("Codex"));
    }

    [Fact]
    public async Task The_phone_sends_a_picked_agent_only_to_a_PC_that_has_it_ready()
    {
        var now = DateTimeOffset.UtcNow;
        _workers.Upsert("pc-a", new AiWorkerRecord
        {
            Device = "PC-A", Agents = ["Grok"], SeenAt = now,
            Offers = [new AiWorkerAgent { Name = "Grok", Kind = "grok" }, new AiWorkerAgent { Name = "Claude Code", Kind = "claude-code", Problem = "signed out" }],
        });
        _workers.Upsert("pc-b", new AiWorkerRecord
        {
            Device = "PC-B", Agents = ["Claude Code"], SeenAt = now.AddSeconds(-30),
            Offers = [new AiWorkerAgent { Name = "Claude Code", Kind = "claude-code", Models = ["claude-haiku-5-5"] }],
        });
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");

        Assert.Null(phone.UnavailableFor("Claude Code"));
        Assert.Equal("No Helm PC online has Codex.", phone.UnavailableFor("Codex"));
        var run = phone.RunAsync(Job("Claude Code"), TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(100);
        var sent = Assert.Single(_jobs.All());
        Assert.Equal("pc-b", sent.Value.To);
        Assert.Equal("Claude Code", sent.Value.Job!.Agent);
        _jobs.SetRemote(sent.Id, sent.Value with { State = AgentJobs.Claimed });
        _jobs.SetRemote(sent.Id, sent.Value with { State = AgentJobs.Done, Result = new AiJobResult("ok", [], [], null) { Agent = "Claude Code" } });
        Assert.Equal("ok", (await run).Text);

        // Grok is on PC-A only, and Claude Code there is signed out: the sheet lists both, with why.
        var offers = phone.Offers;
        Assert.Contains(offers, o => o.Device == "PC-A" && o.Agent is { Name: "Claude Code", Problem: "signed out" });
    }

    [Fact]
    public async Task A_PC_agent_picked_and_not_online_never_falls_back_to_the_API()
    {
        var (assistant, api) = Build();
        assistant.Choose(new AssistantPick(AssistantPickKind.Pc, "Grok", AiProviders.Grok, "grok-4.7", "Grok 4.7"));

        var reply = await assistant.Start().SendAsync("phở 45k", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(0, api.Calls);
        Assert.Contains("Grok trên PC không trả lời được", reply.Problem);
        Assert.Contains("Chọn Auto hoặc một model API", reply.Problem);
    }

    [Fact]
    public async Task An_API_model_picked_skips_the_PC()
    {
        var (assistant, api) = Build();
        _workers.Upsert("pc-a", new AiWorkerRecord { Device = "PC-A", Agents = ["Grok"], SeenAt = DateTimeOffset.UtcNow });
        assistant.Choose(new AssistantPick(AssistantPickKind.Api, null, AiProviders.Claude, "claude-haiku-5-5", "Haiku 5.5"));

        var reply = await assistant.Start().SendAsync("phở 45k", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(1, api.Calls);
        Assert.Null(reply.RanOn);
        Assert.Empty(_jobs.All());
    }

    [Fact]
    public void The_sheet_lists_agents_and_API_families_newest_first_and_knows_the_pick()
    {
        var (assistant, _) = Build();
        _workers.Upsert("pc-a", new AiWorkerRecord
        {
            Device = "PC-A", Agents = ["Grok"], SeenAt = DateTimeOffset.UtcNow,
            Offers = [new AiWorkerAgent { Name = "Grok", Kind = "grok", Models = ["grok-4.7-build-fast", "grok-4.7", "grok-5"] }],
        });
        assistant.Choose(new AssistantPick(AssistantPickKind.Pc, "Grok", AiProviders.Grok, "grok-5", "Grok 5"));

        var picker = assistant.Picker();

        var grok = picker.Families[0];
        Assert.Equal((AssistantPickKind.Pc, "Grok", "PC-A"), (grok.Kind, grok.Name, grok.Device));
        Assert.Equal(["Grok 5", "Grok 4.7", "Grok 4.7 build fast"], grok.Versions.Select(v => v.Name));
        Assert.Equal("grok-5", picker.Picked?.Model);
        Assert.Equal(["low", "high"], picker.Efforts);
        // The Claude key's fallback models: one family each, priced per question.
        var api = picker.Families.Where(f => f.Kind == AssistantPickKind.Api).ToList();
        Assert.Equal(["Claude Sonnet", "Claude Haiku"], api.Select(f => f.Name));
        Assert.All(api.SelectMany(f => f.Versions), v => Assert.StartsWith("~", v.Price));
        // Auto keeps the model picked last: Grok runs it (it is a Grok one); the API, with no Grok key, a Claude one.
        assistant.Choose((AssistantPick?)null);
        picker = assistant.Picker();
        Assert.Null(picker.Picked);
        Assert.Equal(["Grok · Grok 5", "Haiku 5.5"], picker.Auto.Select(s => s.Name));
        // From an API pick, Auto has each agent on its own model when the pick is not of its kind.
        assistant.Choose(new AssistantPick(AssistantPickKind.Api, null, AiProviders.Claude, "claude-sonnet-5-5", "Sonnet 5.5"));
        assistant.Choose((AssistantPick?)null);
        Assert.Equal(["Grok · Grok 4.7 build fast", "Sonnet 5.5"], assistant.Picker().Auto.Select(s => s.Name));
    }

    [Theory]
    [InlineData("claude-opus-5-5", null, "low,medium,high,xhigh,max")]
    [InlineData("claude-sonnet-4-6", null, "low,medium,high")]
    [InlineData("claude-haiku-4-5", null, "")]
    [InlineData("grok-4.7", null, "")]
    [InlineData("grok-4.7", "grok", "low,high")]
    [InlineData("", "codex", "")]
    public void Each_model_takes_its_own_effort_levels(string model, string? agent, string levels) =>
        Assert.Equal(levels, string.Join(",", AiEfforts.For(model, agent)));

    [Theory]
    [InlineData("xhigh", "low,high", "high")]
    [InlineData("medium", "low,high", "high")]
    [InlineData("max", "low,medium,high", "high")]
    [InlineData("high", "", "")]
    public void The_slider_goes_to_the_nearest_level_the_model_takes(string effort, string taken, string nearest) =>
        Assert.Equal(nearest, AiEfforts.Nearest(effort, taken.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    private (HelmAssistant Assistant, Api Api) Build()
    {
        var keys = new AiKeyStore(_keys);
        keys.Add(AiProviders.Claude, "Helm", ClaudeKey);
        var api = new Api();
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), _wallet);
        var assistant = new HelmAssistant(keys, _features, new AiUsageBook(_usage), [new WalletMcpTools(debts, _wallet)], [api], _ => true, new AiRouter([phone]));
        return (assistant, api);
    }

    /// <summary>An agent's chats: what model each was made for; every turn answers "ok".</summary>
    private sealed class Recorder : IAssistantChatFactory, IAssistantChat
    {
        public string? Model { get; private set; }
        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            Model = model;
            return this;
        }

        public void AddUser(string text) { }

        public Task<AssistantStep> NextAsync(CancellationToken ct) => Task.FromResult(new AssistantStep("ok", [], new AssistantUsage(100, 10, 0, 0), false));

        public void AddResults(IReadOnlyList<ToolOutcome> results) { }
    }

    private sealed class Api : IAssistantChatFactory
    {
        public int Calls { get; private set; }
        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools) => new Chat(this);

        private sealed class Chat(Api api) : IAssistantChat
        {
            public void AddUser(string text) { }

            public Task<AssistantStep> NextAsync(CancellationToken ct)
            {
                api.Calls++;
                return Task.FromResult(new AssistantStep("API answered.", [], new AssistantUsage(100, 10, 0, 0), false));
            }

            public void AddResults(IReadOnlyList<ToolOutcome> results) { }
        }
    }
}
