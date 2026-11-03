using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Ai.Auto;
using Helm.Core.Links;
using Helm.Core.Mcp;
using Helm.Modules.Habits;
using Helm.Modules.Missions;
using Helm.Modules.Notes;
using Helm.Modules.Tracker;
using Helm.Modules.Vocab;
using Helm.Modules.Wallet;
using Helm.Modules.Workspace;

namespace Helm.Tests;

/// <summary>
/// What every Ask Helm call carries before the user's words: the instructions and the tools <see cref="ToolScope"/>
/// picks, on the API (system + tool definitions) and on a PC's agent (<see cref="CliAgentProtocol.Preamble"/>). The
/// budgets below keep the sizes from growing back unnoticed; <c>HELM_TOKENS_OUT=&lt;file&gt;</c> writes the table.
/// </summary>
public sealed class AskHelmTokensTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));

    /// <summary>The typical requests, and the conversation each one is a turn of (a follow-up comes after the first).</summary>
    private static readonly (string Name, string[] Turns)[] Requests =
    [
        ("phở 45k", ["trưa nay ăn phở 45k"]),
        ("Nam mượn 200k", ["Nam mượn anh 200k"]),
        ("tháng này tiêu", ["tháng này tiêu bao nhiêu"]),
        ("nhắc 9h mai", ["nhắc anh 9h mai gọi khách"]),
        ("ghi chú", ["ghi chú: mua quà sinh nhật mẹ trước thứ bảy"]),
        ("chạy bộ", ["anh chạy bộ rồi"]),
        ("báo thức 6h", ["6h sáng mai gọi anh dậy"]),
        ("tiền điện 450k", ["đóng tiền điện rồi 450k"]),
        ("chia lẩu", ["chia tiền lẩu 1tr2 4 người"]),
        ("kiểm tra tin", ["Kiểm tra tin này: uống nước chanh buổi sáng chữa được ung thư"]),
        ("ảnh (photo)", []),
        ("follow-up", ["trưa nay ăn phở 45k", "à mà 50k chứ"]),
    ];

    /// <summary>
    /// Characters to tokens, roughly (no API key here for count_tokens): Claude's tokenizer takes about 3.5 ASCII
    /// characters a token in English and JSON, and about 1.5 characters a token in Vietnamese with its marks.
    /// </summary>
    internal static int Tokens(string text)
    {
        int ascii = 0, other = 0;
        foreach (var c in text)
            if (c < 128) ascii++;
            else other++;
        return (int)Math.Round(ascii / 3.5 + other / 1.5);
    }

    /// <summary>A tool as the API gets it: its name, description and input schema.</summary>
    internal static string ApiText(AssistantTool tool) => tool.Name + tool.Description + tool.Schema.ToJsonString(Unescaped);

    /// <summary>JSON as the model reads it: the API decodes "đ" before the model sees it.</summary>
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal sealed record Row(string Request, int Tools, int Instructions, int ToolChars, int PcChars)
    {
        public int ApiChars => Instructions + ToolChars;
    }

    /// <summary>Each request's tools and sizes, as a turn of a new card would send them.</summary>
    internal IReadOnlyList<(Row Row, string Api, string Pc)> Measure()
    {
        var all = Assistant().OfferedTools([new FakePhone()]);
        var system = HelmAssistant.Instructions;
        var rows = new List<(Row, string, string)>();
        foreach (var (name, turns) in Requests)
        {
            var scope = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyList<AssistantTool> tools = [];
            if (turns.Length == 0)
            {
                // A photo: its groups, whatever the words.
                scope.UnionWith(AssistantPhotos.Groups);
                tools = ToolScope.Pick(all, scope, "", known: true);
            }
            foreach (var turn in turns)
                // A post to check goes to the web with none of Helm's tools.
                tools = WebSearch.IsFactCheck(turn) ? [] : ToolScope.Pick(all, scope, turn);
            var api = system + "\n" + string.Join("\n", tools.Select(ApiText));
            // A PC is offered no tool that waits for a yes, nor the phone's clipboard (it cannot read the phone).
            var pcTools = tools.Where(t => t.Mode != AssistantToolMode.Confirm && !(t.Source.ReadOnly && t.Name.StartsWith(HelmAssistant.PhoneToolPrefix, StringComparison.Ordinal))).ToList();
            var pc = CliAgentProtocol.Preamble(system, pcTools);
            rows.Add((new Row(name, tools.Count, system.Length, api.Length - system.Length, pc.Length), api, pc));
        }
        return rows;
    }

    /// <summary>A few typical changes, run for real: each one's whole result and what the model reads of it.</summary>
    private async Task<List<(string Tool, string Whole, string Brief)>> Results()
    {
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), wallet, _time);
        var bills = new BillBook(new MemorySynced<WalletBill>(), wallet, _time);
        bills.Add("Tiền điện", null, 5, null);
        var tracker = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
        tracker.AddWorkspace("Việc");
        var tools = new IMcpToolProvider[]
        {
            new WalletMcpTools(debts, wallet), new WalletShareTools(debts, wallet), new BillMcpTools(bills, wallet), new TrackerMcpTools(tracker),
            new MissionsMcpTools(new MissionsStore(new MemorySynced<Mission>(), new MemorySynced<MissionStep>(), new MemorySyncedLog<MissionEvent>(), _time)),
        }.SelectMany(p => p.Tools).ToDictionary(t => t.Name);
        (string, string)[] calls =
        [
            ("wallet_add_transaction", """{"amount":"45k","direction":"spend","description":"Phở","category":"Food & drinks"}"""),
            ("wallet_add_debt", """{"person":"Nam","amount":"200k","kind":"owes_me"}"""),
            ("wallet_split_bill", """{"total":"1tr2","description":"Lẩu","people":4,"owing":["Nam","Tú"],"paid":["Huy"]}"""),
            ("bill_pay", """{"bill":"điện","amount":"450k"}"""),
            ("tracker_add_task", """{"title":"Gọi khách","due":"2026-11-04T09:00"}"""),
            ("mission_create", """{"title":"Aptis B1","deadline":"2026-11-15","start":true,"phases":[{"title":"Nghe","steps":[{"title":"Luyện nghe 1","doneWhen":"8/10","estimateDays":1},{"title":"Ôn tập","estimateDays":0.5}]},{"title":"Thi","steps":[{"title":"Thi thử","estimateDays":1},{"title":"Thi Aptis","estimateDays":1}]}]}"""),
        ];
        var results = new List<(string, string, string)>();
        foreach (var (name, input) in calls)
        {
            var whole = HelmAssistant.Serialize(await tools[name].Run(Json(input), CancellationToken.None));
            results.Add((name, whole, AssistantLoop.ForModel(tools[name], whole)));
        }
        return results;
    }

    [Fact]
    public async Task The_sizes_are_written_out_when_asked()
    {
        var rows = Measure();
        if (Environment.GetEnvironmentVariable("HELM_TOKENS_OUT") is not { Length: > 0 } file) return;
        var all = Assistant().OfferedTools([new FakePhone()]);
        var text = new StringBuilder()
            .AppendLine($"instructions: {HelmAssistant.Instructions.Length} chars, ~{Tokens(HelmAssistant.Instructions)} tokens")
            .AppendLine("| Request | Tools | API chars | API ~tokens | PC chars | PC ~tokens |")
            .AppendLine("|---|---|---|---|---|---|");
        foreach (var (row, api, pc) in rows)
            text.AppendLine($"| {row.Request} | {row.Tools} | {api.Length} | {Tokens(api)} | {pc.Length} | {Tokens(pc)} |");
        text.AppendLine().AppendLine("Each tool (API: name + description + schema):");
        foreach (var tool in all) text.AppendLine($"  {tool.Name}: {ApiText(tool).Length} chars, ~{Tokens(ApiText(tool))} tokens");
        text.AppendLine().AppendLine("Results (whole, as the model reads it):");
        foreach (var (tool, whole, brief) in await Results()) text.AppendLine($"  {tool}: {whole.Length} → {brief.Length} chars");
        // What the photo's call carries (the most tools), as the API and a PC's agent get it.
        var photo = rows.Single(r => r.Row.Request.StartsWith("ảnh", StringComparison.Ordinal));
        text.AppendLine().AppendLine("API, photo turn:").AppendLine(photo.Api).AppendLine().AppendLine("PC, photo turn:").AppendLine(photo.Pc);
        File.WriteAllText(file, text.ToString());
    }

    /// <summary>
    /// The most each request may carry (characters on the API, on a PC). Were 7480 / 8442 for "phở 45k" and 14458 / 15517
    /// for a photo before the short texts (docs/ai-keys.md, "Tokens per call"): raise one only for a reason.
    /// </summary>
    private static readonly Dictionary<string, (int Api, int Pc)> Budgets = new()
    {
        ["phở 45k"] = (5300, 4700), ["Nam mượn 200k"] = (5300, 4700), ["tháng này tiêu"] = (5300, 4700), ["nhắc 9h mai"] = (4300, 3700),
        ["ghi chú"] = (3500, 3400), ["chạy bộ"] = (2900, 3000), ["báo thức 6h"] = (4100, 3600), ["tiền điện 450k"] = (6400, 5400),
        ["chia lẩu"] = (5300, 4700), ["kiểm tra tin"] = (1700, 1800), ["ảnh (photo)"] = (9700, 7600), ["follow-up"] = (5300, 4700),
    };

    [Fact]
    public void Each_call_stays_within_its_budget()
    {
        foreach (var (row, api, pc) in Measure())
        {
            var (apiMax, pcMax) = Budgets[row.Request];
            Assert.True(api.Length <= apiMax, $"{row.Request}: {api.Length} characters on the API, the budget is {apiMax}.");
            Assert.True(pc.Length <= pcMax, $"{row.Request}: {pc.Length} characters on a PC, the budget is {pcMax}.");
        }
    }

    [Fact]
    public void Every_Ask_Helm_tool_has_its_short_text()
    {
        var offered = Assistant().OfferedTools([new FakePhone()]);
        // The kit has every module Ask Helm offers, so a new tool cannot slip past.
        Assert.Empty(HelmAssistant.Tools.Except(offered.Select(t => t.Name)));
        foreach (var tool in offered.Where(t => !t.Name.StartsWith(HelmAssistant.PhoneToolPrefix, StringComparison.Ordinal)))
        {
            Assert.True(tool.Source.Brief is { Length: > 0 }, $"{tool.Name} has no Brief: Ask Helm would send its whole MCP text on every call.");
            var full = new AssistantTool(tool.Source with { Brief = null }, tool.Mode);
            Assert.True(ApiText(tool).Length < ApiText(full).Length, $"{tool.Name}'s short texts are not shorter than its MCP ones.");
        }
    }

    [Fact]
    public void The_short_schema_keeps_types_enums_required_and_hints_while_MCP_clients_keep_the_full_texts()
    {
        var offered = Assistant().OfferedTools([]).ToDictionary(t => t.Name);
        var add = offered["wallet_add_transaction"];
        var properties = add.Schema["properties"]!;
        Assert.NotEqual(add.Source.Description, add.Description);
        Assert.Null(properties["direction"]!["description"]);
        Assert.Equal(["spend", "income"], properties["direction"]!["enum"]!.AsArray().Select(v => (string?)v));
        Assert.Equal("đồng: 45000, 45k, 1tr2", (string?)properties["amount"]!["description"]);
        Assert.Contains("amount", add.Schema["required"]!.AsArray().Select(v => (string?)v));
        Assert.Contains(AssistantTool.SummaryArgument, add.Schema["required"]!.AsArray().Select(v => (string?)v));
        // The tool itself is untouched: what an MCP client lists.
        Assert.NotNull(add.Source.InputSchema["properties"]!["direction"]!["description"]);

        // A property named "description" stays; only the keyword goes, at any depth.
        var step = offered["mission_create"].Schema["properties"]!["phases"]!["items"]!["properties"]!["steps"]!["items"]!["properties"]!;
        Assert.NotNull(step["description"]);
        Assert.Null(step["description"]!["description"]);
        Assert.NotNull(step["resources"]!["items"]!["anyOf"]);
    }

    [Fact]
    public void A_PC_agent_reads_each_tool_as_a_short_signature_and_results_one_line_each()
    {
        var offered = Assistant().OfferedTools([]).ToDictionary(t => t.Name);

        Assert.StartsWith("{amount: string (đồng: 45000, 45k, 1tr2), direction: \"spend\"|\"income\", description: string, category?: string (",
            CliAgentProtocol.Signature(offered["wallet_add_transaction"].Schema));
        Assert.Contains("tasks: {title: string, notes?: string, due?: string}[] (in order", CliAgentProtocol.Signature(offered["tracker_add_tasks"].Schema));
        Assert.Contains("resources?: (string | {label?: string", CliAgentProtocol.Signature(offered["mission_create"].Schema));
        Assert.Equal("Tool results:\nc1: {\"id\":\"t1\"}\nc2 (error): There is no task x.",
            CliAgentProtocol.Results([new ToolOutcome("c1", """{"id":"t1"}""", false), new ToolOutcome("c2", "There is no task x.", true)]));
    }

    [Fact]
    public async Task The_model_reads_a_short_result_while_the_records_and_Undo_keep_the_whole_one()
    {
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), wallet, _time);
        var bills = new BillBook(new MemorySynced<WalletBill>(), wallet, _time);
        var bill = bills.Add("Tiền điện", null, 5, null);
        IMcpToolProvider[] providers = [new WalletMcpTools(debts, wallet), new WalletShareTools(debts, wallet), new BillMcpTools(bills, wallet)];
        var tools = Assistant().OfferedTools([]).Where(t => t.Name.StartsWith("wallet_") || t.Name.StartsWith("bill")).ToList();
        tools = [.. providers.SelectMany(p => p.Tools).Where(t => tools.Any(o => o.Name == t.Name)).Select(t => new AssistantTool(t, HelmAssistant.ModeOf(t)))];
        var chat = new OneStep(
            new ToolCall("c1", "wallet_add_transaction", Json("""{"amount":"45k","direction":"spend","description":"Phở","summary":"Chi 45.000đ · Phở"}""")),
            new ToolCall("c2", "bill_pay", Json("""{"bill":"điện","amount":"450k","summary":"Chi 450.000đ · Tiền điện"}""")),
            new ToolCall("c3", "wallet_split_bill", Json("""{"total":"1tr2","description":"Lẩu","people":4,"owing":["Nam"],"summary":"Chia lẩu"}""")));

        var outcome = await AssistantLoop.RunAsync(chat, tools, _ => Task.FromResult(true), _ => { }, () => null, phoneHere: true, "Haiku", CancellationToken.None);

        var sent = chat.Results.ToDictionary(r => r.CallId, r => JsonNode.Parse(r.Content)!.AsObject());
        Assert.Equal(["id", "amount", "when"], sent["c1"].Select(p => p.Key));
        Assert.Equal(["name", "month", "amount", "next_due"], sent["c2"].Select(p => p.Key));
        Assert.Equal(["each", "mine", "records"], sent["c3"].Select(p => p.Key));
        Assert.Equal(["summary"], sent["c3"]["records"]![0]!.AsObject().Select(p => p.Key));
        // The records (what a PC hands back for Undo) are whole.
        Assert.Contains("\"transaction_id\"", outcome.Records.Single(r => r.Tool == "bill_pay").ResultJson);
        Assert.Contains("\"currency\"", outcome.Records.Single(r => r.Tool == "wallet_add_transaction").ResultJson);

        foreach (var done in outcome.Done) await done.UndoAsync(CancellationToken.None);
        Assert.Empty(wallet.Transactions());
        Assert.Empty(bills.Get(bill)!.PaidMonths);
        Assert.Empty(debts.Open());
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A model that makes these calls once, then answers.</summary>
    private sealed class OneStep(params ToolCall[] calls) : IAssistantChat
    {
        private bool _called;

        public List<ToolOutcome> Results { get; } = [];

        public void AddUser(string text) { }

        public Task<AssistantStep> NextAsync(CancellationToken ct)
        {
            var step = _called ? new AssistantStep("Xong.", [], new AssistantUsage(1, 1, 0, 0), false) : new AssistantStep("", calls, new AssistantUsage(1, 1, 0, 0), false);
            _called = true;
            return Task.FromResult(step);
        }

        public void AddResults(IReadOnlyList<ToolOutcome> results) => Results.AddRange(results);
    }

    internal HelmAssistant Assistant()
    {
        var links = new LinkHub(new MemorySynced<HelmLink>(), () => [], _time);
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), wallet, _time);
        var vocab = new VocabStore(new MemorySynced<VocabWord>(), new MemorySynced<VocabPart>(), new MemorySyncedLog<VocabReview>(), _time);
        IMcpToolProvider[] providers =
        [
            new WalletMcpTools(debts, wallet),
            new WalletShareTools(debts, wallet),
            new BillMcpTools(new BillBook(new MemorySynced<WalletBill>(), wallet, _time), wallet),
            new TrackerMcpTools(new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time)),
            new NotesMcpTools(new NotesStore(new MemorySynced<NoteItem>(), _time), links),
            new HabitsMcpTools(new HabitsStore(new MemorySynced<Habit>(), new MemorySynced<HabitCheck>(), new MemorySynced<HabitLapse>(), _time, TimeZoneInfo.Utc)),
            new MissionsMcpTools(new MissionsStore(new MemorySynced<Mission>(), new MemorySynced<MissionStep>(), new MemorySyncedLog<MissionEvent>(), _time)),
            new VocabMcpTools(vocab, new VocabQuiz(vocab, new Random(1))),
            new WorkspaceAskTools(new MemorySynced<WorkspaceHostRecord>(), new MemorySynced<WorkspaceSessionRecord>(), new MemorySynced<WorkspaceCommand>(),
                new MemorySynced<WorkspaceResultRecord>(), time: _time),
            new AiBriefTools(new AiBriefBook(new MemorySynced<AiBrief>(), _time), new MemorySettings<AiFeatureSettings>(), () => []),
        ];
        return new HelmAssistant(new AiKeyStore(new MemorySynced<AiKey>()), new MemorySettings<AiFeatureSettings>(), new AiUsageBook(new MemorySyncedLog<AiUsageEntry>()),
            providers, [], _ => true, new AiRouter([]));
    }
}
