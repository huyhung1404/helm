using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>
/// Ask Helm on the web (A8, M4): off until turned on, only for requests that need it, always on the API with Claude's
/// web search (never a PC agent), the search fee counted with the tokens, a Grok pick handing the turn to Claude.
/// </summary>
public sealed class WebSearchTests
{
    private const string ClaudeKey = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa";
    private const string GrokKey = "xai-AbCdEfGhIjKlMnOpQrStUvWxYz0123b2Zz";

    private readonly MemorySynced<AiKey> _keys = new();
    private readonly MemorySyncedLog<AiUsageEntry> _usage = new();
    private readonly MemorySettings<AiFeatureSettings> _features = new();
    private readonly WalletStore _wallet = new(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>());

    private HelmAssistant Assistant(IEnumerable<IAssistantChatFactory> factories, IAgentRoute? pc = null, bool grok = false)
    {
        var keys = new AiKeyStore(_keys);
        keys.Add(AiProviders.Claude, "Helm", ClaudeKey, new AiKeyCheck(DateTimeOffset.UtcNow, AiKeyStatus.Works, "Works.",
            [new AiModel("claude-haiku-5-5", "Claude Haiku 5.5"), new AiModel("claude-sonnet-5-5", "Claude Sonnet 5.5")]));
        if (grok)
            keys.Add(AiProviders.Grok, "Helm", GrokKey, new AiKeyCheck(DateTimeOffset.UtcNow, AiKeyStatus.Works, "Works.", [new AiModel("grok-4.7", "Grok 4.7")]));
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), _wallet);
        return new HelmAssistant(keys, _features, new AiUsageBook(_usage), [new WalletMcpTools(debts, _wallet)], factories, _ => true,
            new AiRouter(pc is null ? [] : [pc]));
    }

    private void TurnOn(decimal cap = 0.5m) =>
        _features.Update(s => s.Features[AiFeatureIds.WebSearch] = new AiFeatureChoice { Enabled = true, Route = AiRoutePolicy.ApiOnly, DailyCapUsd = cap });

    private static Task<bool> Yes(IReadOnlyList<PendingAction> _) => Task.FromResult(true);

    [Theory]
    [InlineData("thời tiết Hà Nội hôm nay")]
    [InlineData("tỷ giá đô hôm nay bao nhiêu")]
    [InlineData("giá vàng SJC")]
    [InlineData("có tin tức gì mới không")]
    [InlineData("ngày mai ở Đà Lạt có mưa không")]
    [InlineData("Kiểm tra tin này: uống nước chanh chữa ung thư")]
    public void Requests_about_the_world_now_need_the_web(string text) => Assert.True(WebSearch.Asks(text));

    [Theory]
    [InlineData("trưa nay ăn phở 45k")]
    [InlineData("nhắc anh mua sữa lúc 5 giờ")]
    [InlineData("hôm nay ở nhà ăn phở 45k")]
    [InlineData("gửi tin nhắn cho Nam")]
    [InlineData("bản tin sáng nay")]
    public void Helms_own_requests_do_not(string text) => Assert.False(WebSearch.Asks(text));

    [Fact]
    public void Web_search_is_off_until_turned_on_and_runs_on_the_API()
    {
        var choice = AiFeatureCatalog.Effective(new AiFeatureSettings(), AiFeatureIds.WebSearch);

        Assert.False(choice.Enabled);
        Assert.Equal(AiRoutePolicy.ApiOnly, choice.Route);
        Assert.Contains(AiFeatureCatalog.All, f => f.Id == AiFeatureIds.WebSearch && !f.Automatic && f.Description.Contains("$0.01"));
        // The other features still start on.
        Assert.True(AiFeatureCatalog.Effective(new AiFeatureSettings(), AiFeatureIds.TextActions).Enabled);
    }

    [Fact]
    public async Task While_off_a_weather_question_gets_no_web_and_a_post_to_check_says_how_to_turn_it_on()
    {
        var api = new Scripted(AiProviders.Claude, Text("Em không xem được thời tiết."));
        var assistant = Assistant([api]);

        var reply = await assistant.Start().SendAsync("thời tiết Hà Nội hôm nay", Yes, CancellationToken.None);

        Assert.Null(reply.Problem);
        Assert.Equal(0, Assert.Single(api.Chats).WebSearches);
        Assert.DoesNotContain(api.Chats[0].Tools, t => t.Name == WebSearch.LookupName);

        var check = await assistant.Start().SendAsync(WebSearch.FactCheckRequest("Uống nước chanh chữa ung thư"), Yes, CancellationToken.None);
        Assert.Equal(WebSearch.Off, check.Problem);
        Assert.Single(api.Chats);
    }

    [Fact]
    public async Task Turned_on_a_web_question_goes_to_the_API_with_web_search_and_never_to_a_PC()
    {
        TurnOn();
        var pc = new Pc();
        var api = new Scripted(AiProviders.Claude, Text("Hôm nay vàng SJC bán 120 triệu một lượng. Nguồn: SJC.", new AssistantUsage(2000, 100, 0, 0, 2)));
        var assistant = Assistant([api], pc);

        var reply = await assistant.Start().SendAsync("giá vàng hôm nay", Yes, CancellationToken.None);

        Assert.Null(reply.Problem);
        Assert.Null(reply.RanOn);
        Assert.Empty(pc.Jobs);
        var chat = Assert.Single(api.Chats);
        Assert.Equal(WebSearch.MaxUses, chat.WebSearches);
        // No word of Helm's tools: none of them is sent.
        Assert.Empty(chat.Tools);
        Assert.Contains("Web search is on for this request", chat.Users[0]);
        Assert.Contains("giá vàng hôm nay", chat.Users[0]);

        // Tokens and two searches, counted as web search's (its own daily cap).
        var entry = Assert.Single(_usage.All()).Value;
        Assert.Equal(AiFeatureIds.WebSearch, entry.Feature);
        Assert.Equal(2, entry.WebSearches);
        Assert.Equal(AiPrices.Usd("claude-haiku-5-5", 2000, 100, 0, 0) + 2 * AiPrices.WebSearchUsd, entry.Usd);

        // A request of Helm's own still goes to the PC first.
        await assistant.Start().SendAsync("phở 45k", Yes, CancellationToken.None);
        Assert.Single(pc.Jobs);
    }

    [Fact]
    public async Task A_post_to_check_asks_for_a_verdict_with_sources_and_sends_no_tools()
    {
        TurnOn();
        var api = new Scripted(AiProviders.Claude, Text("Sai: chưa có nghiên cứu nào cho thấy vậy. Nguồn: Bộ Y tế, VnExpress."));
        var assistant = Assistant([api]);

        var reply = await assistant.Start().SendAsync(WebSearch.FactCheckRequest("Chuyển 500k tiền phí để nhận quà, uống chanh chữa ung thư"), Yes,
            CancellationToken.None);

        Assert.StartsWith("Sai", reply.Text);
        var chat = Assert.Single(api.Chats);
        // The post's words ("tiền", "500k") are not a request for the Wallet.
        Assert.Empty(chat.Tools);
        Assert.Contains("Check this post with web search", chat.Users[0]);
        Assert.Contains("Chưa kiểm chứng được", chat.Users[0]);
    }

    [Fact]
    public async Task The_days_cap_of_web_search_stops_it()
    {
        TurnOn(cap: 0.01m);
        var api = new Scripted(AiProviders.Claude, Text("Trời nắng.", new AssistantUsage(100, 10, 0, 0, 1)), Text("Trời mưa."));
        var assistant = Assistant([api]);

        await assistant.Start().SendAsync("thời tiết Huế", Yes, CancellationToken.None);
        var reply = await assistant.Start().SendAsync("thời tiết Hà Nội", Yes, CancellationToken.None);

        Assert.Contains("limit for web search", reply.Problem);
        Assert.Single(api.Chats);
    }

    [Fact]
    public async Task A_Grok_pick_asks_Claude_for_a_web_turn_and_says_so()
    {
        TurnOn();
        var claude = new Scripted(AiProviders.Claude, Text("Hà Nội 28 độ, nắng. Nguồn: nchmf.gov.vn."));
        var grok = new Scripted(AiProviders.Grok);
        var assistant = Assistant([claude, grok], grok: true);
        assistant.Choose(assistant.Models().First(m => m.Provider == AiProviders.Grok));

        var reply = await assistant.Start().SendAsync("thời tiết Hà Nội", Yes, CancellationToken.None);

        Assert.Empty(grok.Chats);
        Assert.Single(claude.Chats);
        Assert.StartsWith("Grok 4.7 không tìm web qua API được nên câu này em hỏi", reply.Text);
        Assert.EndsWith("Hà Nội 28 độ, nắng. Nguồn: nchmf.gov.vn.", reply.Text);
        Assert.Equal("claude-haiku-5-5", Assert.Single(_usage.All()).Value.Model);
    }

    [Fact]
    public async Task With_web_search_on_Claude_may_turn_it_on_itself()
    {
        TurnOn();
        var api = new Scripted(AiProviders.Claude,
            Call("1", WebSearch.LookupName, """{"why":"lịch chiếu"}"""),
            Text("Tối nay CGV có 3 suất. Nguồn: cgv.vn.", new AssistantUsage(500, 50, 0, 0, 1)));
        var assistant = Assistant([api]);

        var reply = await assistant.Start().SendAsync("tối nay CGV Vincom chiếu phim gì", Yes, CancellationToken.None);

        Assert.Equal("Tối nay CGV có 3 suất. Nguồn: cgv.vn.", reply.Text);
        var chat = Assert.Single(api.Chats);
        Assert.Contains(chat.Added, t => t.Name == WebSearch.LookupName);
        Assert.Equal(WebSearch.MaxUses, chat.WebSearches);
        // From the search on, the calls are web search's.
        Assert.Equal([AiFeatureIds.AskHelm, AiFeatureIds.WebSearch], _usage.All().Select(e => e.Value.Feature));
    }

    [Fact]
    public async Task Claudes_request_carries_the_web_search_tool_and_the_search_goes_back_unchanged()
    {
        TurnOn();
        var bodies = new List<string>();
        var answers = new Queue<string>([
            """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","stop_reason":"end_turn","stop_sequence":null,
             "content":[
               {"type":"text","text":"Để em tìm."},
               {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"giá vàng SJC hôm nay"}},
               {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[
                 {"type":"web_search_result","url":"https://sjc.com.vn/gia-vang","title":"Giá vàng SJC","encrypted_content":"ENC123","page_age":"3 Nov 2026"}]},
               {"type":"text","text":"Hôm nay vàng miếng bán ","citations":null},
               {"type":"text","text":"120 triệu một lượng","citations":[{"type":"web_search_result_location","url":"https://www.sjc.com.vn/gia-vang",
                 "title":"Giá vàng SJC","encrypted_index":"IDX9","cited_text":"Vàng SJC 120.000.000"}]},
               {"type":"text","text":"."}],
             "usage":{"input_tokens":3000,"output_tokens":120,"cache_read_input_tokens":0,"cache_creation_input_tokens":0,
               "server_tool_use":{"web_search_requests":1}}}
            """,
            """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-haiku-5-5","stop_reason":"end_turn","stop_sequence":null,
             "content":[{"type":"text","text":"Dạ."}],
             "usage":{"input_tokens":100,"output_tokens":5,"cache_read_input_tokens":3000,"cache_creation_input_tokens":0}}
            """,
        ]);
        var http = new HttpClient(new Fake(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return answers.Dequeue();
        }));
        var assistant = Assistant([new ClaudeAssistantChatFactory(http)]);
        var session = assistant.Start();

        var reply = await session.SendAsync("giá vàng SJC hôm nay", Yes, CancellationToken.None);

        // Only the answer after the search, in one piece, with the site it cites.
        Assert.Null(reply.Problem);
        Assert.Equal("Hôm nay vàng miếng bán 120 triệu một lượng. Nguồn: sjc.com.vn.", reply.Text);
        var tool = JsonNode.Parse(bodies[0])!["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "web_search")!;
        // Haiku takes the basic search, at most three a call.
        Assert.Equal("web_search_20250305", tool["type"]!.GetValue<string>());
        Assert.Equal(WebSearch.MaxUses, tool["max_uses"]!.GetValue<int>());
        var entry = Assert.Single(_usage.All()).Value;
        Assert.Equal(1, entry.WebSearches);
        Assert.Equal(AiPrices.Usd("claude-haiku-5-5", 3000, 120, 0, 0) + AiPrices.WebSearchUsd, entry.Usd);

        // The next turn sends the search, its encrypted results and the citation back as they came.
        await session.SendAsync("cảm ơn em", Yes, CancellationToken.None);
        var history = JsonNode.Parse(bodies[1])!["messages"]!.AsArray();
        var echoed = history[1]!.ToJsonString();
        Assert.Contains("\"srvtoolu_1\"", echoed);
        Assert.Contains("\"web_search_tool_result\"", echoed);
        Assert.Contains("\"ENC123\"", echoed);
        Assert.Contains("\"IDX9\"", echoed);
    }

    [Fact]
    public async Task Sonnet_gets_the_search_with_dynamic_filtering_and_a_paused_turn_goes_on()
    {
        _features.Update(s => s.Features[AiFeatureIds.WebSearch] = new AiFeatureChoice { Enabled = true, Route = AiRoutePolicy.ApiOnly, Model = ClaudeModels.Sonnet });
        var bodies = new List<string>();
        var answers = new Queue<string>([
            """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5","stop_reason":"pause_turn","stop_sequence":null,
             "content":[{"type":"server_tool_use","id":"srvtoolu_2","name":"web_search","input":{"query":"tỷ giá USD Vietcombank"}}],
             "usage":{"input_tokens":800,"output_tokens":30,"cache_read_input_tokens":0,"cache_creation_input_tokens":0,
               "server_tool_use":{"web_search_requests":1}}}
            """,
            """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-sonnet-5-5","stop_reason":"end_turn","stop_sequence":null,
             "content":[{"type":"text","text":"Vietcombank bán 25.400đ một đô. Nguồn: Vietcombank."}],
             "usage":{"input_tokens":900,"output_tokens":40,"cache_read_input_tokens":0,"cache_creation_input_tokens":0,
               "server_tool_use":{"web_search_requests":1}}}
            """,
        ]);
        var http = new HttpClient(new Fake(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return answers.Dequeue();
        }));
        var assistant = Assistant([new ClaudeAssistantChatFactory(http)]);

        var reply = await assistant.Start().SendAsync("tỷ giá đô hôm nay", Yes, CancellationToken.None);

        Assert.Equal("Vietcombank bán 25.400đ một đô. Nguồn: Vietcombank.", reply.Text);
        Assert.Equal(2, bodies.Count);
        var first = JsonNode.Parse(bodies[0])!;
        Assert.Equal("claude-sonnet-5-5", first["model"]!.GetValue<string>());
        Assert.Contains(first["tools"]!.AsArray(), t => t!["type"]!.GetValue<string>() == "web_search_20260209");
        // Sent again with the paused content last, and no new user message.
        var messages = JsonNode.Parse(bodies[1])!["messages"]!.AsArray();
        Assert.Equal("assistant", messages[^1]!["role"]!.GetValue<string>());
        Assert.Contains("srvtoolu_2", messages[^1]!.ToJsonString());
        // One step, both calls, two searches.
        var entry = Assert.Single(_usage.All()).Value;
        Assert.Equal(2, entry.WebSearches);
        Assert.Equal(1700, entry.InputTokens);
    }

    // ---- doubles ----------------------------------------------------------------------------------------------

    private static AssistantStep Text(string text, AssistantUsage? usage = null) => new(text, [], usage ?? new AssistantUsage(100, 10, 0, 0), false);

    private static AssistantStep Call(string id, string name, string input) =>
        new("", [new ToolCall(id, name, JsonDocument.Parse(input).RootElement.Clone())], new AssistantUsage(100, 10, 0, 0), false);

    private sealed class Scripted(AiProvider provider, params AssistantStep[] steps) : IAssistantChatFactory
    {
        private readonly Queue<AssistantStep> _steps = new(steps);

        public List<Chat> Chats { get; } = [];

        public AiProvider Provider => provider;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            var chat = new Chat(_steps, tools, provider == AiProviders.Claude);
            Chats.Add(chat);
            return chat;
        }

        public sealed class Chat(Queue<AssistantStep> steps, IReadOnlyList<AssistantTool> tools, bool searches) : IAssistantChat
        {
            public IReadOnlyList<AssistantTool> Tools { get; } = tools;
            public List<AssistantTool> Added { get; } = [];
            public List<string> Users { get; } = [];

            /// <summary>The max_uses web search was added with; 0: never.</summary>
            public int WebSearches { get; private set; }

            public void AddUser(string text) => Users.Add(text);

            public void AddTools(IReadOnlyList<AssistantTool> tools) => Added.AddRange(tools);

            public bool AddWebSearch(int maxUses)
            {
                if (searches) WebSearches = maxUses;
                return searches;
            }

            public Task<AssistantStep> NextAsync(CancellationToken ct) =>
                Task.FromResult(steps.Count > 0 ? steps.Dequeue() : new AssistantStep("", [], new AssistantUsage(0, 0, 0, 0), false));

            public void AddResults(IReadOnlyList<ToolOutcome> results) { }
        }
    }

    private sealed class Pc : IAgentRoute
    {
        public List<AiJob> Jobs { get; } = [];
        public string Device => "PC-HUNG";
        public string? Unavailable() => null;

        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            Jobs.Add(job);
            return Task.FromResult(new AiJobResult("Em ghi rồi.", [], [], 0m));
        }

        public Task UndoAsync(AiJobAction action, CancellationToken ct) => Task.CompletedTask;

        public void End(string conversationId) { }
    }

    private sealed class Fake(Func<HttpRequestMessage, Task<string>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await answer(request), System.Text.Encoding.UTF8, "application/json") };
    }
}
