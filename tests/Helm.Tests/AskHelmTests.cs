using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>Ask Helm's assistant: which tools it offers, what runs at once, what waits for a yes, Undo, the cap, the cost.</summary>
public sealed class AskHelmTests
{
    private const string ClaudeKey = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa";

    private readonly MemorySynced<AiKey> _keys = new();
    private readonly MemorySyncedLog<AiUsageEntry> _usage = new();
    private readonly MemorySettings<AiFeatureSettings> _features = new();
    private readonly WalletStore _wallet = new(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>());

    private HelmAssistant Assistant(ScriptedFactory factory, params IMcpToolProvider[] extra)
    {
        var keys = new AiKeyStore(_keys);
        keys.Add(AiProviders.Claude, "Helm", ClaudeKey, new AiKeyCheck(DateTimeOffset.UtcNow, AiKeyStatus.Works, "Works.",
            [new AiModel("claude-haiku-5-5", "Claude Haiku 5.5"), new AiModel("claude-sonnet-5-5", "Claude Sonnet 5.5")]));
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), _wallet);
        IMcpToolProvider[] providers = [new WalletMcpTools(debts, _wallet), .. extra];
        return new HelmAssistant(keys, _features, new AiUsageBook(_usage), providers, [factory], _ => true);
    }

    [Fact]
    public async Task A_spending_is_saved_at_once_and_can_be_undone()
    {
        var factory = new ScriptedFactory(
            Step(Call("1", "wallet_add_transaction", """{"amount":"45k","direction":"spend","description":"Phở","summary":"Chi 45.000đ · Phở"}""")),
            Step(text: "Em ghi rồi nha Anh."));
        var assistant = Assistant(factory);
        var asked = false;

        var reply = await assistant.Start().SendAsync("trưa nay ăn phở 45 nghìn", _ => { asked = true; return Task.FromResult(true); }, CancellationToken.None);

        Assert.Null(reply.Problem);
        Assert.False(asked);
        Assert.Equal("Em ghi rồi nha Anh.", reply.Text);
        var action = Assert.Single(reply.Done);
        Assert.Equal("Chi 45.000đ · Phở", action.Summary);
        Assert.True(action.CanUndo);
        var saved = Assert.Single(_wallet.Transactions());
        Assert.Equal(-45000m, saved.Value.Amount);
        // The summary is for the card: the tool never saw it.
        Assert.DoesNotContain("summary", factory.Chats[0].Results[0][0].Content);

        await action.UndoAsync(CancellationToken.None);
        Assert.Empty(_wallet.Transactions());
        Assert.False(action.CanUndo);
    }

    [Fact]
    public async Task A_change_that_cannot_be_undone_waits_for_a_yes()
    {
        var tool = new FakeTool();
        var factory = new ScriptedFactory(
            Step(Call("1", "tracker_start_task", """{"summary":"Xoá thứ gì đó"}""")),
            Step(text: "Em chưa làm."));
        var assistant = Assistant(factory, tool);
        IReadOnlyList<PendingAction>? pending = null;

        var reply = await assistant.Start().SendAsync("xoá đi", p => { pending = p; return Task.FromResult(false); }, CancellationToken.None);

        Assert.Equal("Xoá thứ gì đó", Assert.Single(pending!).Summary);
        Assert.Equal(0, tool.Runs);
        Assert.True(reply.Declined);
        Assert.Empty(reply.Done);
        Assert.Contains("said no", factory.Chats[0].Results[0][0].Content);
    }

    [Fact]
    public async Task Reading_runs_freely_and_only_Ask_Helm_tools_are_offered()
    {
        _wallet.AddManual(-30000m, "Cà phê", DateTimeOffset.Now, null);
        var factory = new ScriptedFactory(Step(Call("1", "wallet_summary", "{}")), Step(text: "Tháng này Anh chi 30.000đ."));
        var assistant = Assistant(factory, new FakeTool(), new RemoteTool());

        var reply = await assistant.Start().SendAsync("tháng này tiêu bao nhiêu", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal("Tháng này Anh chi 30.000đ.", reply.Text);
        Assert.Empty(reply.Done);
        Assert.Contains("\"spent\":30000", factory.Chats[0].Results[0][0].Content);
        var offered = factory.Chats[0].Tools.Select(t => t.Name).ToList();
        Assert.Contains("wallet_summary", offered);
        Assert.Contains("tracker_start_task", offered);
        Assert.DoesNotContain("phone_remote", offered);
        Assert.Contains("summary", factory.Chats[0].Tools.Single(t => t.Name == "wallet_add_transaction").Schema["required"]!.ToJsonString());
        Assert.StartsWith("Now: ", factory.Chats[0].Users[0]);
    }

    [Fact]
    public async Task Every_call_is_counted_and_the_days_cap_stops_it()
    {
        var factory = new ScriptedFactory(Step(text: "Dạ.", usage: new AssistantUsage(1_000_000, 0, 0, 0)));
        var assistant = Assistant(factory);

        await assistant.Start().SendAsync("chào", _ => Task.FromResult(true), CancellationToken.None);

        var entry = Assert.Single(_usage.All()).Value;
        Assert.Equal("claude-haiku-5-5", entry.Model);
        Assert.Equal(0.10m, entry.Usd);
        _features.Update(s => s.Features[AiFeatureIds.AskHelm] = new AiFeatureChoice { DailyCapUsd = 0.05m });
        var reply = await assistant.Start().SendAsync("nữa", _ => Task.FromResult(true), CancellationToken.None);
        Assert.Contains("limit", reply.Problem);
    }

    [Fact]
    public void Without_a_key_it_says_where_to_add_one()
    {
        var assistant = new HelmAssistant(new AiKeyStore(new MemorySynced<AiKey>()), _features, new AiUsageBook(_usage), [], [new ScriptedFactory()], _ => true);

        Assert.Contains("AI → Models & keys", assistant.Unavailable());
    }

    [Fact]
    public void The_models_are_the_chat_models_of_tested_keys_and_the_pick_is_kept()
    {
        var keys = new AiKeyStore(_keys);
        keys.Add(AiProviders.Grok, "Helm", "xai-AbCdEfGhIjKlMnOpQrStUvWxYz0123b2Zz", new AiKeyCheck(DateTimeOffset.UtcNow, AiKeyStatus.Works, "Works.",
            [new AiModel("grok-4.7", "grok-4.7"), new AiModel("grok-imagine-image", "grok-imagine-image")]));
        var assistant = new HelmAssistant(keys, _features, new AiUsageBook(_usage), [],
            [new ScriptedFactory(AiProviders.Claude), new ScriptedFactory(AiProviders.Grok)], _ => true);

        var models = assistant.Models();
        Assert.Equal(["grok-4.7"], models.Select(m => m.Model));
        assistant.Choose(models[0]);
        Assert.Equal("xai", assistant.Choice.Provider);
        Assert.Equal("grok-4.7", assistant.Current()!.Model);
    }

    [Fact]
    public async Task Claude_tool_calls_go_through_the_SDK_and_back_with_their_results()
    {
        var bodies = new List<string>();
        var answers = new Queue<string>([
            """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","stop_reason":"tool_use","stop_sequence":null,
             "content":[{"type":"text","text":"Để em ghi."},{"type":"tool_use","id":"toolu_1","name":"wallet_add_transaction",
             "input":{"amount":"45k","direction":"spend","description":"Phở","summary":"Chi 45.000đ · Phở"}}],
             "usage":{"input_tokens":1200,"output_tokens":80,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}
            """,
            """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-haiku-5-5","stop_reason":"end_turn","stop_sequence":null,
             "content":[{"type":"text","text":"Xong rồi Anh."}],
             "usage":{"input_tokens":100,"output_tokens":10,"cache_read_input_tokens":1200,"cache_creation_input_tokens":0}}
            """,
        ]);
        var http = new HttpClient(new Fake(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return answers.Dequeue();
        }));
        var keys = new AiKeyStore(_keys);
        keys.Add(AiProviders.Claude, "Helm", ClaudeKey);
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), _wallet);
        var assistant = new HelmAssistant(keys, _features, new AiUsageBook(_usage), [new WalletMcpTools(debts, _wallet)],
            [new ClaudeAssistantChatFactory(http)], _ => true);

        var reply = await assistant.Start().SendAsync("phở 45k", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Null(reply.Problem);
        Assert.Equal("Xong rồi Anh.", reply.Text);
        Assert.Single(_wallet.Transactions());
        Assert.Equal(2, bodies.Count);
        var second = JsonNode.Parse(bodies[1])!;
        Assert.Equal("claude-haiku-5-5", second["model"]!.GetValue<string>());
        Assert.Contains("tool_result", second["messages"]!.ToJsonString());
        Assert.Contains("toolu_1", second["messages"]!.ToJsonString());
        Assert.Equal(2, _usage.All().Count);
    }

    // ---- doubles ----------------------------------------------------------------------------------------------

    private static AssistantStep Step(ToolCall? call = null, string text = "", AssistantUsage? usage = null) =>
        new(text, call is null ? [] : [call], usage ?? new AssistantUsage(100, 10, 0, 0), false);

    private static ToolCall Call(string id, string name, string input) => new(id, name, JsonDocument.Parse(input).RootElement.Clone());

    private sealed class ScriptedFactory(AiProvider? provider = null, params AssistantStep[] steps) : IAssistantChatFactory
    {
        public ScriptedFactory(params AssistantStep[] steps) : this(null, steps) { }

        public List<ScriptedChat> Chats { get; } = [];

        public AiProvider Provider { get; } = provider ?? AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            var chat = new ScriptedChat(new Queue<AssistantStep>(steps), tools);
            Chats.Add(chat);
            return chat;
        }
    }

    private sealed class ScriptedChat(Queue<AssistantStep> steps, IReadOnlyList<AssistantTool> tools) : IAssistantChat
    {
        public IReadOnlyList<AssistantTool> Tools { get; } = tools;
        public List<string> Users { get; } = [];
        public List<IReadOnlyList<ToolOutcome>> Results { get; } = [];

        public void AddUser(string text) => Users.Add(text);

        public Task<AssistantStep> NextAsync(CancellationToken ct) =>
            Task.FromResult(steps.Count > 0 ? steps.Dequeue() : new AssistantStep("", [], new AssistantUsage(0, 0, 0, 0), false));

        public void AddResults(IReadOnlyList<ToolOutcome> results) => Results.Add(results);
    }

    private sealed class FakeTool : IMcpToolProvider
    {
        public int Runs { get; private set; }

        public string? ModuleId => null;

        public IEnumerable<McpTool> Tools =>
        [
            new("tracker_start_task", "Changes something for good.", McpTool.NoArguments(), (_, _) => { Runs++; return Task.FromResult<object?>("done"); }),
        ];
    }

    private sealed class RemoteTool : IMcpToolProvider
    {
        public string? ModuleId => null;

        public IEnumerable<McpTool> Tools =>
            [new("phone_remote", "Runs on a server.", McpTool.NoArguments(), (_, _) => Task.FromResult<object?>("ran")) { Risk = McpRisk.Remote }];
    }

    private sealed class Fake(Func<HttpRequestMessage, Task<string>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await answer(request), System.Text.Encoding.UTF8, "application/json") };
    }
}

/// <summary>Synced settings kept in memory.</summary>
internal sealed class MemorySettings<T> : ISyncedSettings<T> where T : class, IVersionedSettings, new()
{
    public T Current { get; } = new();

    public event EventHandler<T>? Changed;

    public void Update(Action<T> mutate)
    {
        mutate(Current);
        Changed?.Invoke(this, Current);
    }

    public void Flush() { }
}
