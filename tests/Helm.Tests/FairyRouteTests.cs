using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Connector.Fairy;
using Helm.Connector.Mcp;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Modules.Wallet;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The phone's agent jobs on the box: the connector takes them from Helm Sync as a Helm PC does and hands each one to
/// Fairy Core over its socket's host methods (helm/jobs/hello, next, result), Undo running on the connector's own tools.
/// </summary>
public sealed class FairyRouteTests
{
    private readonly WalletStore _wallet = new(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>());
    private readonly MemorySynced<AiJobRecord> _jobs = new();
    private readonly MemorySynced<AiWorkerRecord> _workers = new();

    private IMcpToolProvider[] Providers => [new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), _wallet), _wallet)];

    private static AiJob Job(string text = "phá»Ÿ 45k") =>
        new(AiFeatureIds.AskHelm, "conv-1", text, ["wallet_add_transaction"], [], "claude-haiku-5-5", "low");

    [Fact]
    public async Task APhoneJobGoesThroughSyncToFairyCoreAndItsAnswerComesBack()
    {
        var route = new FairyRoute(() => Providers, "fairy");
        var server = ConnectorMcp.Server(Providers, new ConfirmationGate(), NullLogger.Instance, route.Methods());
        var link = new FairyLink(route, () => new AgentJobWorker(_jobs, _workers, route, () => "box-id", route.Device, route.Ready), NullLogger.Instance);
        link.Start();
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");
        Assert.NotNull(phone.Unavailable());
        Assert.False(link.Working);

        // Fairy Core arrives with its agents: the phone sees the box and its picker lists them.
        await Rpc(server, FairyRoute.Hello, new JsonObject
        {
            ["agents"] = JsonSerializer.SerializeToNode(new[] { new AiWorkerAgent { Name = "Claude Code", Kind = "claude-code", Models = ["claude-haiku-5-5"] } }, HelmJson.Options),
        });
        Assert.True(link.Working);
        Assert.Null(phone.Unavailable());
        Assert.Equal("fairy", phone.Device);
        Assert.Equal("Claude Code", Assert.Single(phone.Offers).Agent.Name);

        // Core answers the next job it is handed.
        var core = Task.Run(async () =>
        {
            var next = await Rpc(server, FairyRoute.Next, new JsonObject { ["wait"] = 5 });
            var job = next["result"]!["job"]!;
            Assert.Equal("run", job["kind"]!.GetValue<string>());
            Assert.Equal("phá»Ÿ 45k", job["job"]!["text"]!.GetValue<string>());
            var result = new AiJobResult("Em ghi rá»“i.", [new AiJobAction("wallet_add_transaction", "Chi 45.000Ä‘", """{"id":"t1"}""")], [], 0.002m)
            {
                Agent = "Claude Code",
            };
            await Rpc(server, FairyRoute.Result, new JsonObject { ["id"] = job["id"]!.DeepClone(), ["result"] = JsonSerializer.SerializeToNode(result, HelmJson.Options) });
        });

        var answer = await phone.RunAsync(Job(), TimeSpan.FromSeconds(10), CancellationToken.None);
        await core;

        Assert.Equal("Em ghi rá»“i.", answer.Text);
        Assert.Equal("Claude Code", answer.Agent);
        Assert.Equal("Chi 45.000Ä‘", Assert.Single(answer.Done).Summary);
        link.Dispose();
        Assert.Empty(_workers.All());
    }

    [Fact]
    public async Task AJobCoreNeverTookGivesWayAndOneItTookIsNotRunAgain()
    {
        var route = new FairyRoute(() => Providers, "fairy");
        var server = ConnectorMcp.Server(Providers, new ConfirmationGate(), NullLogger.Instance, route.Methods());
        Assert.Contains("not connected", (await Assert.ThrowsAsync<AiRouteException>(() => route.RunAsync(Job(), TimeSpan.FromSeconds(1), default))).Message,
            StringComparison.Ordinal);
        await Rpc(server, FairyRoute.Hello, new JsonObject { ["agents"] = new JsonArray(new JsonObject { ["name"] = "Codex" }) });

        // Nobody asked for it in time: the phone may go on to the next way.
        var waiting = await Assert.ThrowsAsync<AiRouteException>(() => route.RunAsync(Job(), TimeSpan.FromMilliseconds(200), default));
        Assert.IsNotType<AiRouteTakenException>(waiting);
        // That job is never handed out afterwards.
        Assert.Null((await Rpc(server, FairyRoute.Next, new JsonObject { ["wait"] = 0.3 }))["result"]!["job"]);

        // Taken (Core said it got it), then no answer: it may have done part of it, so it is not run elsewhere.
        var run = route.RunAsync(Job(), TimeSpan.FromMilliseconds(800), default);
        var handed = await Rpc(server, FairyRoute.Next, new JsonObject { ["wait"] = 2 });
        Assert.NotNull(handed["result"]!["job"]);
        await Rpc(server, FairyRoute.Next, new JsonObject { ["wait"] = 0.1, ["received"] = new JsonArray(handed["result"]!["job"]!["id"]!.DeepClone()) });
        await Assert.ThrowsAsync<AiRouteTakenException>(() => run);
        var late = await Rpc(server, FairyRoute.Result, new JsonObject { ["id"] = handed["result"]!["job"]!["id"]!.DeepClone(), ["error"] = "x" });
        Assert.Contains("ran out of time", late["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJobHandedToACoreThatWentAwayGoesToTheNextPoll()
    {
        var time = new SteppedTime();
        var route = new FairyRoute(() => Providers, "fairy", time);
        var server = ConnectorMcp.Server(Providers, new ConfirmationGate(), NullLogger.Instance, route.Methods());
        await Rpc(server, FairyRoute.Hello, new JsonObject { ["agents"] = new JsonArray(new JsonObject { ["name"] = "Codex" }) });
        var run = route.RunAsync(Job(), TimeSpan.FromSeconds(30), default);

        // A Core killed while its poll waited: the job went to it, and no "received" ever comes.
        var lost = (await Rpc(server, FairyRoute.Next, new JsonObject { ["wait"] = 2 }))["result"]!["job"]!;
        time.Advance(FairyRoute.AckWithin + TimeSpan.FromSeconds(1));

        // The Core that came back gets it, and answers.
        var again = (await Rpc(server, FairyRoute.Next, new JsonObject { ["wait"] = 2 }))["result"]!["job"]!;
        Assert.Equal(lost["id"]!.GetValue<string>(), again["id"]!.GetValue<string>());
        await Rpc(server, FairyRoute.Result, new JsonObject
        {
            ["id"] = again["id"]!.DeepClone(), ["result"] = JsonSerializer.SerializeToNode(new AiJobResult("Xong.", [], [], null), HelmJson.Options),
        });
        Assert.Equal("Xong.", (await run).Text);
    }

    /// <summary>A clock whose timestamps move only when told (the route measures waits with them).</summary>
    private sealed class SteppedTime : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks) + global::System.Diagnostics.Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / global::System.Diagnostics.Stopwatch.Frequency;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    [Fact]
    public async Task UndoRunsOnTheConnectorsOwnTools()
    {
        var route = new FairyRoute(() => Providers, "fairy");
        var server = ConnectorMcp.Server(Providers, new ConfirmationGate(), NullLogger.Instance, route.Methods());
        var added = await Rpc(server, "tools/call", new JsonObject
        {
            ["name"] = "wallet_add_transaction",
            ["arguments"] = new JsonObject { ["amount"] = "45k", ["direction"] = "spend", ["description"] = "Phá»Ÿ" },
        });
        var result = added["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Single(_wallet.Transactions());

        // The tool says it can be taken back, and helm/undo does.
        var list = await Rpc(server, "tools/list", null);
        var listed = list["result"]!["tools"]!.AsArray().First(t => t!["name"]!.GetValue<string>() == "wallet_add_transaction")!;
        Assert.True(listed["_meta"]![McpServer.UndoableMeta]!.GetValue<bool>());
        await Rpc(server, FairyRoute.Undo, new JsonObject { ["tool"] = "wallet_add_transaction", ["result"] = JsonNode.Parse(result) });
        Assert.Empty(_wallet.Transactions());

        // The phone's Undo of a record Fairy Core made comes the same way.
        await Rpc(server, "tools/call", new JsonObject
        {
            ["name"] = "wallet_add_transaction",
            ["arguments"] = new JsonObject { ["amount"] = "30k", ["direction"] = "spend", ["description"] = "CÃ  phÃª" },
        });
        var again = _wallet.Transactions().Single();
        await route.UndoAsync(new AiJobAction("wallet_add_transaction", "Chi 30.000Ä‘", $$"""{"id":"{{again.Id}}"}"""), default);
        Assert.Empty(_wallet.Transactions());
    }

    [Fact]
    public async Task PhotosTravelBesideTheJob()
    {
        var route = new FairyRoute(() => Providers, "fairy");
        var server = ConnectorMcp.Server(Providers, new ConfirmationGate(), NullLogger.Instance, route.Methods());
        await Rpc(server, FairyRoute.Hello, new JsonObject { ["agents"] = new JsonArray(new JsonObject { ["name"] = "Codex" }), ["readsPhotos"] = true });
        Assert.True(route.ReadsPhotos);

        var run = route.RunAsync(Job() with { Images = [new AssistantImage([1, 2, 3], "image/png")] }, TimeSpan.FromMilliseconds(500), default);
        var job = (await Rpc(server, FairyRoute.Next, new JsonObject { ["wait"] = 2 }))["result"]!["job"]!;

        Assert.Equal("image/png", job["images"]![0]!["mediaType"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), job["images"]![0]!["data"]!.GetValue<string>());
        Assert.Null(job["job"]!["images"]);
        await Assert.ThrowsAnyAsync<AiRouteException>(() => run);
    }

    [Fact]
    public async Task ACallThatWaitsForTheUsersYesIsNeitherDoneNorRecorded()
    {
        var asked = 0;
        var gated = new McpTool("notes_trash", "Moves a note to the trash.", McpTool.NoArguments(), (_, _) =>
        {
            asked++;
            return Task.FromResult<object?>(new { status = McpTool.NeedsConfirmation, summary = "Move the note â€œOldâ€ to the trash." });
        }) { Destructive = true, ConfirmsItself = true };
        var chat = new TwoSteps("""{"summary":"XoÃ¡ ghi chÃº Old"}""", "Em sáº½ xoÃ¡ ghi chÃº â€œOldâ€, Anh xÃ¡c nháº­n nhÃ©?");
        using var route = new LocalAgentRoute("fairy", "Fake", chat, [new Gated([gated])], _ => true, () => null, offers: _ => true);

        var result = await route.RunAsync(Job() with { Tools = ["notes_trash"] }, TimeSpan.FromSeconds(5), default);

        Assert.Equal(1, asked);
        Assert.Empty(result.Done);
        // A second step lets the model ask in its own words, instead of "done".
        Assert.Equal("Em sáº½ xoÃ¡ ghi chÃº â€œOldâ€, Anh xÃ¡c nháº­n nhÃ©?", result.Text);
        Assert.Contains("notes_trash", chat.Offered);
    }

    private sealed class Gated(IEnumerable<McpTool> tools) : IMcpToolProvider
    {
        public string? ModuleId => null;

        public IEnumerable<McpTool> Tools => tools;
    }

    /// <summary>Calls notes_trash, then answers with the text it was given.</summary>
    private sealed class TwoSteps(string arguments, string answer) : IAssistantChatFactory, IAssistantChat
    {
        private int _step;

        public List<string> Offered { get; } = [];

        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            Offered.AddRange(tools.Select(t => t.Name));
            return this;
        }

        public void AddUser(string text)
        {
        }

        public void AddResults(IReadOnlyList<ToolOutcome> results)
        {
        }

        public Task<AssistantStep> NextAsync(CancellationToken ct) => Task.FromResult(_step++ == 0
            ? new AssistantStep("", [new ToolCall("c1", "notes_trash", JsonDocument.Parse(arguments).RootElement.Clone())], new AssistantUsage(10, 5, 0, 0), false)
            : new AssistantStep(answer, [], new AssistantUsage(10, 5, 0, 0), false));
    }

    private static async Task<JsonObject> Rpc(McpServer server, string method, JsonObject? parameters)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters };
        return (await server.HandleAsync(request.ToJsonString(), default))!;
    }
}
