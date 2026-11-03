using System.Text.Json;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>Ask Helm through the router: Claude Code on a Helm PC first (free), the API only when no PC answers in time.</summary>
public sealed class AiRouterTests
{
    private const string ClaudeKey = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa";

    private readonly MemorySynced<AiKey> _keys = new();
    private readonly MemorySyncedLog<AiUsageEntry> _usage = new();
    private readonly MemorySettings<AiFeatureSettings> _features = new();
    private readonly WalletStore _wallet = new(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>());

    private (HelmAssistant Assistant, Api Api) Build(IAgentRoute? pc, bool withKey = true, params IMcpToolProvider[] phone)
    {
        var keys = new AiKeyStore(_keys);
        if (withKey) keys.Add(AiProviders.Claude, "Helm", ClaudeKey);
        var api = new Api();
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), _wallet);
        var assistant = new HelmAssistant(keys, _features, new AiUsageBook(_usage), [new WalletMcpTools(debts, _wallet)], [api], _ => true,
            new AiRouter(pc is null ? [] : [pc]));
        return (assistant, api);
    }

    [Fact]
    public async Task A_PC_that_answers_in_time_is_used_and_the_API_is_not()
    {
        var pc = new Pc(new AiJobResult("Em ghi rồi.", [new AiJobAction("wallet_add_transaction", "Chi 45.000đ · Phở", """{"id":"t1"}""")], [], 0.002m));
        var (assistant, api) = Build(pc);

        var reply = await assistant.Start().SendAsync("phở 45k", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal("Em ghi rồi.", reply.Text);
        Assert.Equal("PC-HUNG", reply.RanOn);
        Assert.Equal(0, api.Calls);
        var job = Assert.Single(pc.Jobs);
        Assert.Contains("wallet_add_transaction", job.Tools);
        Assert.StartsWith("Now: ", job.Text);
        var entry = Assert.Single(_usage.All()).Value;
        Assert.Equal(AiUsageBook.ClaudeCodeRoute, entry.Route);
        Assert.Equal(0m, entry.Usd);
        Assert.Equal(0.002m, entry.SavedUsd);

        // Undo goes back to the PC that made it.
        await Assert.Single(reply.Done).UndoAsync(CancellationToken.None);
        Assert.Equal("wallet_add_transaction", Assert.Single(pc.Undone).Tool);
    }

    [Fact]
    public async Task A_PC_too_slow_hands_over_to_the_API()
    {
        var pc = new Pc(null);
        var (assistant, api) = Build(pc);

        var reply = await assistant.Start().SendAsync("chào", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal("API answered.", reply.Text);
        Assert.Null(reply.RanOn);
        Assert.Single(pc.Jobs);
        Assert.Equal(1, api.Calls);
        Assert.Equal(AiUsageBook.ApiRoute, Assert.Single(_usage.All()).Value.Route);
    }

    [Fact]
    public async Task Api_only_never_waits_for_a_PC_and_a_Grok_pick_still_asks_it_first()
    {
        var pc = new Pc(new AiJobResult("PC.", [], [], null));
        var (assistant, _) = Build(pc);
        _features.Update(s => s.Features[AiFeatureIds.AskHelm] = new AiFeatureChoice { Route = AiRoutePolicy.ApiOnly });

        await assistant.Start().SendAsync("chào", _ => Task.FromResult(true), CancellationToken.None);
        Assert.Empty(pc.Jobs);

        // A Grok pick is only the fallback: the PC's agent still answers first.
        _features.Update(s => s.Features[AiFeatureIds.AskHelm] = new AiFeatureChoice { Provider = "xai", Model = "grok-4.7" });
        await assistant.Start().SendAsync("chào", _ => Task.FromResult(true), CancellationToken.None);
        Assert.Single(pc.Jobs);
    }

    [Fact]
    public async Task Pc_only_with_no_PC_says_so_and_spends_nothing()
    {
        var pc = new Pc(null) { Problem = "offline" };
        var (assistant, api) = Build(pc);
        _features.Update(s => s.Features[AiFeatureIds.AskHelm] = new AiFeatureChoice { Route = AiRoutePolicy.PcOnly });

        var reply = await assistant.Start().SendAsync("chào", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Contains("PC-HUNG: offline", reply.Problem);
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task Without_an_API_key_a_PC_still_answers()
    {
        var pc = new Pc(new AiJobResult("PC.", [], [], null));
        var (assistant, _) = Build(pc, withKey: false);

        Assert.Null(assistant.Unavailable());
        var reply = await assistant.Start().SendAsync("chào", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal("PC.", reply.Text);
    }

    [Fact]
    public async Task A_follow_up_stays_on_the_PC_and_moves_to_the_API_with_what_was_said()
    {
        var pc = new Pc(new AiJobResult("Bao nhiêu tiền?", [], [], null));
        var (assistant, api) = Build(pc);
        var session = assistant.Start();

        await session.SendAsync("anh nợ Nam", _ => Task.FromResult(true), CancellationToken.None);
        pc.Next = null;
        await session.SendAsync("300 nghìn", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(2, pc.Jobs.Count);
        Assert.Equal(pc.Jobs[0].ConversationId, pc.Jobs[1].ConversationId);
        Assert.Equal("300 nghìn", pc.Jobs[1].Text);
        Assert.Contains("anh nợ Nam", api.LastUser);
        Assert.Contains("Bao nhiêu tiền?", api.LastUser);
    }

    [Fact]
    public async Task What_only_the_phone_can_do_comes_back_and_runs_on_the_phone()
    {
        var alarm = new PhoneAlarm();
        var pc = new Pc(new AiJobResult("Em đặt báo thức rồi.", [], [new AiJobPhoneAction("phone_set_alarm", """{"time":"06:30"}""", "Báo thức 06:30")], null));
        var (assistant, _) = Build(pc);

        var reply = await assistant.Start([alarm]).SendAsync("6 rưỡi gọi anh dậy", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal("06:30", alarm.Set);
        Assert.Equal("Báo thức 06:30", Assert.Single(reply.Done).Summary);
        Assert.Contains("phone_set_alarm", pc.Jobs[0].PhoneTools);
        Assert.DoesNotContain("phone_set_alarm", pc.Jobs[0].Tools);
    }

    // ---- doubles ----------------------------------------------------------------------------------------------

    private sealed class Pc(AiJobResult? next) : IAgentRoute
    {
        public AiJobResult? Next { get; set; } = next;
        public string? Problem { get; init; }
        public List<AiJob> Jobs { get; } = [];
        public List<AiJobAction> Undone { get; } = [];
        public string Device => "PC-HUNG";
        public string? Unavailable() => Problem;

        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            Jobs.Add(job);
            return Next is { } result ? Task.FromResult(result) : throw new AiRouteException("timed out");
        }

        public Task UndoAsync(AiJobAction action, CancellationToken ct)
        {
            Undone.Add(action);
            return Task.CompletedTask;
        }

        public void End(string conversationId) { }
    }

    private sealed class Api : IAssistantChatFactory
    {
        public int Calls { get; private set; }
        public string LastUser { get; private set; } = "";
        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools) => new Chat(this);

        private sealed class Chat(Api api) : IAssistantChat
        {
            public void AddUser(string text) => api.LastUser = text;

            public Task<AssistantStep> NextAsync(CancellationToken ct)
            {
                api.Calls++;
                return Task.FromResult(new AssistantStep("API answered.", [], new AssistantUsage(100, 10, 0, 0), false));
            }

            public void AddResults(IReadOnlyList<ToolOutcome> results) { }
        }
    }

    private sealed class PhoneAlarm : IMcpToolProvider
    {
        public string? Set { get; private set; }
        public string? ModuleId => null;

        public IEnumerable<McpTool> Tools =>
        [
            new("phone_set_alarm", "Alarm.", McpTool.NoArguments(), (args, _) =>
            {
                Set = args.GetProperty("time").GetString();
                return Task.FromResult<object?>(new { alarm = Set });
            }),
        ];
    }
}
