using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Ai.Auto;
using Helm.Core.Mcp;
using Helm.Core.Sync;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>
/// Ask Helm's photo to data: the photo as Claude's and Grok's APIs take it, Sonnet when the picked model cannot read
/// it, the size it is sent at; the brief chip of the card; and the "Nói" + two categories of Wallet's notification.
/// </summary>
public sealed class AskHelmPhotoTests
{
    private const string ClaudeKey = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa";
    private const string GrokKey = "xai-AbCdEfGhIjKlMnOpQrStUvWxYz0123b2Zz";
    private static readonly AssistantImage Photo = new([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
    private static readonly TimeZoneInfo Ict = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");

    private readonly MemorySynced<AiKey> _keys = new();
    private readonly MemorySyncedLog<AiUsageEntry> _usage = new();
    private readonly MemorySettings<AiFeatureSettings> _features = new();
    private readonly WalletStore _wallet = new(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>());

    private const string SavedPho = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","stop_reason":"tool_use","stop_sequence":null,
         "content":[{"type":"tool_use","id":"toolu_1","name":"wallet_add_transaction",
         "input":{"amount":"100k","direction":"spend","description":"Phở Thìn","summary":"Chi 100.000đ · Phở Thìn"}}],
         "usage":{"input_tokens":1800,"output_tokens":60,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}
        """;

    private HelmAssistant Assistant(IEnumerable<IAssistantChatFactory> factories, bool claude = true, string? grokModel = null)
    {
        var keys = new AiKeyStore(_keys);
        if (claude)
            keys.Add(AiProviders.Claude, "Helm", ClaudeKey, new AiKeyCheck(DateTimeOffset.UtcNow, AiKeyStatus.Works, "Works.",
                [new AiModel("claude-haiku-5-5", "Claude Haiku 5.5"), new AiModel("claude-sonnet-5-5", "Claude Sonnet 5.5")]));
        if (grokModel is not null)
            keys.Add(AiProviders.Grok, "Helm", GrokKey, new AiKeyCheck(DateTimeOffset.UtcNow, AiKeyStatus.Works, "Works.", [new AiModel(grokModel, grokModel)]));
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), _wallet);
        return new HelmAssistant(keys, _features, new AiUsageBook(_usage), [new WalletMcpTools(debts, _wallet)], factories, _ => true);
    }

    // ---- the photo on each API --------------------------------------------------------------------------------

    [Fact]
    public async Task Claude_gets_the_photo_as_an_image_block_before_the_words_and_the_next_turn_goes_on_without_it()
    {
        var bodies = new List<string>();
        var answers = new Queue<string>([SavedPho, """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-haiku-5-5","stop_reason":"end_turn","stop_sequence":null,
             "content":[{"type":"text","text":"Dạ."}],"usage":{"input_tokens":100,"output_tokens":5}}
            """]);
        var http = new HttpClient(new Fake(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return (HttpStatusCode.OK, answers.Dequeue());
        }));
        var session = Assistant([new ClaudeAssistantChatFactory(http)]).Start();

        var reply = await session.SendPhotoAsync(Photo, "chia đôi với Nam", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Null(reply.Problem);
        Assert.Equal("Chi 100.000đ · Phở Thìn", Assert.Single(reply.Done).Summary);
        Assert.Single(_wallet.Transactions());
        var content = JsonNode.Parse(bodies[0])!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("image", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("base64", content[0]!["source"]!["type"]!.GetValue<string>());
        Assert.Equal("image/jpeg", content[0]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Photo.Data), content[0]!["source"]!["data"]!.GetValue<string>());
        Assert.Equal("text", content[1]!["type"]!.GetValue<string>());
        Assert.Contains("chia đôi với Nam", content[1]!["text"]!.GetValue<string>());
        Assert.Equal("claude-haiku-5-5", session.PhotoModel!.Model);

        // The photo's chat is gone: the next turn is a new chat with what was done, as words.
        await session.SendAsync("cảm ơn em", _ => Task.FromResult(true), CancellationToken.None);
        var next = JsonNode.Parse(bodies[1])!;
        Assert.Single(next["messages"]!.AsArray());
        Assert.DoesNotContain("\"image\"", bodies[1]);
        Assert.DoesNotContain(Convert.ToBase64String(Photo.Data), bodies[1]);
        Assert.Contains("[Chi 100.000đ · Phở Thìn]", next["messages"]![0]!["content"]!.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    [Fact]
    public async Task Grok_gets_the_photo_as_a_data_uri()
    {
        string? body = null;
        var http = new HttpClient(new Fake(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return (HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"Đây là ảnh con mèo."}}],"usage":{"prompt_tokens":900,"completion_tokens":9}}""");
        }));
        var assistant = Assistant([new GrokAssistantChatFactory(http)], claude: false, grokModel: "grok-4.7");

        var reply = await assistant.Start().SendPhotoAsync(Photo, "", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal("Đây là ảnh con mèo.", reply.Text);
        var user = JsonNode.Parse(body!)!["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("image_url", user[0]!["type"]!.GetValue<string>());
        Assert.Equal($"data:image/jpeg;base64,{Convert.ToBase64String(Photo.Data)}", user[0]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("text", user[1]!["type"]!.GetValue<string>());
        Assert.Contains("without a word", user[1]!["text"]!.GetValue<string>());
    }

    // ---- a model that cannot read it ---------------------------------------------------------------------------

    [Fact]
    public async Task A_photo_Claude_turns_down_is_read_once_more_with_Sonnet_and_the_answer_says_so()
    {
        var models = new List<string>();
        var http = new HttpClient(new Fake(async request =>
        {
            var model = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["model"]!.GetValue<string>();
            models.Add(model);
            return model == "claude-haiku-5-5"
                ? (HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"This model does not support image input."}}""")
                : (HttpStatusCode.OK, SavedPho);
        }));
        var session = Assistant([new ClaudeAssistantChatFactory(http)]).Start();

        var reply = await session.SendPhotoAsync(Photo, "", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(["claude-haiku-5-5", "claude-sonnet-5-5"], models);
        Assert.Null(reply.Problem);
        Assert.StartsWith("Haiku 5.5 không đọc được ảnh nên em nhờ Sonnet 5.5.", reply.Text);
        Assert.Single(reply.Done);
        Assert.Equal("claude-sonnet-5-5", session.PhotoModel!.Model);
        // Counted on the model that answered.
        Assert.Equal("claude-sonnet-5-5", Assert.Single(_usage.All()).Value.Model);
    }

    [Fact]
    public async Task A_text_only_model_hands_the_photo_to_Sonnet_without_trying()
    {
        var claude = new Recording(AiProviders.Claude, reads: true);
        var grok = new Recording(AiProviders.Grok, reads: true);
        var assistant = Assistant([claude, grok], grokModel: "grok-3-mini");
        assistant.Choose(assistant.Models().Single(m => m.Model == "grok-3-mini"));

        var reply = await assistant.Start().SendPhotoAsync(Photo, "", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Empty(grok.Models);
        Assert.Equal(["claude-sonnet-5-5"], claude.Models);
        Assert.Single(claude.Images);
        Assert.StartsWith("grok-3-mini không đọc được ảnh nên em nhờ Sonnet 5.5.", reply.Text);
    }

    [Fact]
    public async Task Without_a_Claude_key_a_photo_the_model_cannot_read_is_a_problem()
    {
        var grok = new Recording(AiProviders.Grok, reads: false);
        var assistant = Assistant([grok], claude: false, grokModel: "grok-4.7");

        var reply = await assistant.Start().SendPhotoAsync(Photo, "", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(["grok-4.7"], grok.Models);
        Assert.Contains("cannot read photos", reply.Problem);
        Assert.Empty(reply.Done);
    }

    [Fact]
    public async Task A_photo_goes_to_a_PC_agent_that_reads_it_first_in_a_turn_of_its_own()
    {
        var claude = new Recording(AiProviders.Claude, reads: true);
        var pc = new Pc(reads: true);
        var keys = new AiKeyStore(_keys);
        keys.Add(AiProviders.Claude, "Helm", ClaudeKey);
        var assistant = new HelmAssistant(keys, _features, new AiUsageBook(_usage), [], [claude], _ => true, new AiRouter([pc]));
        var session = assistant.Start();

        var reply = await session.SendPhotoAsync(Photo, "chia đôi", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal("Dạ.", reply.Text);
        Assert.Equal("Codex trên PC-HUNG", reply.RanOn);
        Assert.Empty(claude.Images);
        var job = Assert.Single(pc.Sent);
        Assert.Same(Photo, Assert.Single(job.Images));
        Assert.Contains("chia đôi", job.Text);
        // Its own conversation: the photo's chat is not the one later words go to.
        await session.SendAsync("cảm ơn", _ => Task.FromResult(true), CancellationToken.None);
        Assert.Equal(2, pc.Sent.Count);
        Assert.NotEqual(job.ConversationId, pc.Sent[1].ConversationId);
        Assert.Empty(pc.Sent[1].Images);
        Assert.Contains("(sent a photo) chia đôi", pc.Sent[1].Text);
    }

    [Fact]
    public async Task A_photo_skips_a_PC_whose_agents_read_none()
    {
        var claude = new Recording(AiProviders.Claude, reads: true);
        var pc = new Pc();
        var keys = new AiKeyStore(_keys);
        keys.Add(AiProviders.Claude, "Helm", ClaudeKey);
        var assistant = new HelmAssistant(keys, _features, new AiUsageBook(_usage), [], [claude], _ => true, new AiRouter([pc]));

        await assistant.Start().SendPhotoAsync(Photo, "", _ => Task.FromResult(true), CancellationToken.None);
        Assert.Equal(0, pc.Jobs);
        Assert.Single(claude.Images);

        // Run on a PC only: no API, so no photo.
        _features.Update(s => s.Features[AiFeatureIds.AskHelm] = new AiFeatureChoice { Route = AiRoutePolicy.PcOnly });
        var reply = await assistant.Start().SendPhotoAsync(Photo, "", _ => Task.FromResult(true), CancellationToken.None);
        Assert.Contains("PC only", reply.Problem);
        Assert.Equal(0, pc.Jobs);
    }

    // ---- the size it is sent at ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(8000, 6000, 4)]
    [InlineData(4032, 3024, 2)]
    [InlineData(3136, 100, 2)]
    [InlineData(3135, 100, 1)]
    [InlineData(1200, 900, 1)]
    public void A_photo_is_decoded_at_a_power_of_two_still_as_big_as_it_is_sent(int width, int height, int sample)
    {
        Assert.Equal(sample, AssistantPhotos.SampleSize(width, height));
        Assert.True(Math.Max(width, height) / sample >= Math.Min(Math.Max(width, height), AssistantPhotos.MaxSide));
    }

    [Fact]
    public void A_photo_is_sent_at_most_1568_on_its_longer_side_and_never_enlarged()
    {
        Assert.Equal((1568, 1176), AssistantPhotos.Fit(4000, 3000));
        Assert.Equal((1176, 1568), AssistantPhotos.Fit(2016, 2688));
        Assert.Equal((1000, 500), AssistantPhotos.Fit(1000, 500));
        Assert.Equal((1568, 1), AssistantPhotos.Fit(20000, 10));
    }

    [Theory]
    [InlineData("anthropic", "claude-haiku-5-5", true)]
    [InlineData("xai", "grok-4.7", true)]
    [InlineData("xai", "grok-3-mini", false)]
    [InlineData("xai", "grok-code-fast-1", false)]
    [InlineData("xai", "grok-2-1212", false)]
    [InlineData("xai", "grok-2-vision-1212", true)]
    public void Which_models_read_photos(string provider, string model, bool reads) =>
        Assert.Equal(reads, AssistantPhotos.Reads(AiProviders.All.Single(p => p.Id == provider), model));

    // ---- the brief chip -----------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(7));

    private static SyncedItem<AiBrief> Brief(string id, string kind, DateTimeOffset at) =>
        new(id, new AiBrief { Kind = kind, Title = id, Text = "…", CreatedAt = at }, at);

    [Fact]
    public void The_chip_shows_todays_latest_brief_and_yesterdays_last_behind_Hom_qua()
    {
        var chip = AiBriefChip.Pick([
            Brief("old", AiFeatureIds.EveningReview, Now.AddDays(-2)),
            Brief("yesterday-morning", AiFeatureIds.MorningBrief, new DateTimeOffset(2026, 11, 2, 6, 45, 0, TimeSpan.FromHours(7))),
            Brief("yesterday-evening", AiFeatureIds.EveningReview, new DateTimeOffset(2026, 11, 2, 21, 30, 0, TimeSpan.FromHours(7))),
            Brief("today", AiFeatureIds.MorningBrief, new DateTimeOffset(2026, 11, 3, 6, 45, 0, TimeSpan.FromHours(7))),
            Brief("digest", AiFeatureIds.SpendingDigest, Now.AddMinutes(-5)),
        ], Now, Ict);

        Assert.Equal("today", chip!.Latest.Id);
        Assert.Equal("yesterday-evening", chip.Yesterday!.Id);
        Assert.Equal("Bản tin sáng · 06:45", AiBriefChip.Label(chip.Latest.Value, Now, Ict));
        Assert.Equal("Tổng kết tối · Hôm qua 21:30", AiBriefChip.Label(chip.Yesterday.Value, Now, Ict));
    }

    [Fact]
    public void Yesterdays_brief_alone_is_the_chip_with_nothing_older_and_none_is_no_chip()
    {
        var yesterday = Brief("y", AiFeatureIds.EveningReview, new DateTimeOffset(2026, 11, 2, 21, 30, 0, TimeSpan.FromHours(7)));
        var chip = AiBriefChip.Pick([yesterday, Brief("y-morning", AiFeatureIds.MorningBrief, new DateTimeOffset(2026, 11, 2, 6, 45, 0, TimeSpan.FromHours(7)))], Now, Ict);
        Assert.Equal("y", chip!.Latest.Id);
        Assert.Null(chip.Yesterday);

        // Two days old, or only other jobs' briefs: no chip.
        Assert.Null(AiBriefChip.Pick([Brief("old", AiFeatureIds.MorningBrief, new DateTimeOffset(2026, 11, 1, 23, 59, 0, TimeSpan.FromHours(7)))], Now, Ict));
        Assert.Null(AiBriefChip.Pick([Brief("coach", AiFeatureIds.HabitCoach, Now.AddHours(-1))], Now, Ict));
        Assert.Null(AiBriefChip.Pick([], Now, Ict));
    }

    [Fact]
    public void The_day_is_the_users_own()
    {
        // 23:30 UTC on 2 Nov is 06:30 on 3 Nov in Vietnam: today's.
        var chip = AiBriefChip.Pick([Brief("b", AiFeatureIds.MorningBrief, new DateTimeOffset(2026, 11, 2, 23, 30, 0, TimeSpan.Zero))], Now, Ict);
        Assert.Equal("Bản tin sáng · 06:30", AiBriefChip.Label(chip!.Latest.Value, Now, Ict));
    }

    // ---- W6: "Nói" and the two likeliest categories ------------------------------------------------------------

    [Fact]
    public void The_notification_offers_Noi_and_exactly_the_two_likeliest_categories()
    {
        var suggestions = _wallet.Suggest(new WalletTransaction { Amount = -45_000, Description = "QUAN PHO THIN" }, 3);
        Assert.Equal(3, suggestions.Count);

        var buttons = CategoryButtons.ForNotification(suggestions, null, _wallet.Category);
        Assert.Equal(suggestions.Take(2).Select(s => s.Id), buttons.Select(b => b.CategoryId));

        // AI's pick first, still two in all.
        var picked = CategoryButtons.ForNotification(suggestions, new CategoryProposal(WalletCategories.Travel, null, CategoryKind.Expense, true), _wallet.Category);
        Assert.Equal([WalletCategories.Travel, suggestions[0].Id], picked.Select(b => b.CategoryId));
        Assert.Equal("Nói", CategoryButtons.SayLabel);
    }

    // ---- doubles ------------------------------------------------------------------------------------------------

    /// <summary>Scripted chats that answer "Dạ." and keep the models asked and the photos sent.</summary>
    private sealed class Recording(AiProvider provider, bool reads) : IAssistantChatFactory
    {
        public AiProvider Provider => provider;
        public List<string> Models { get; } = [];
        public List<AssistantImage> Images { get; } = [];

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            Models.Add(model);
            return reads ? new Reader(this) : new TextOnly();
        }

        private sealed class Reader(Recording owner) : IAssistantChat
        {
            public void AddUser(string text) { }

            public void AddUser(string text, AssistantImage image) => owner.Images.Add(image);

            public Task<AssistantStep> NextAsync(CancellationToken ct) => Task.FromResult(new AssistantStep("Dạ.", [], new AssistantUsage(10, 1, 0, 0), false));

            public void AddResults(IReadOnlyList<ToolOutcome> results) { }
        }

        /// <summary>No photo support: the interface's default turns the photo down.</summary>
        private sealed class TextOnly : IAssistantChat
        {
            public void AddUser(string text) { }

            public Task<AssistantStep> NextAsync(CancellationToken ct) => Task.FromResult(new AssistantStep("Dạ.", [], new AssistantUsage(10, 1, 0, 0), false));

            public void AddResults(IReadOnlyList<ToolOutcome> results) { }
        }
    }

    /// <summary>A PC agent that takes any job; a photo only when it reads photos.</summary>
    private sealed class Pc(bool reads = false) : IAgentRoute
    {
        public int Jobs => Sent.Count;

        public List<AiJob> Sent { get; } = [];

        public string Device => "PC-HUNG";

        public bool ReadsPhotos => reads;

        public string? Unavailable() => null;

        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            Sent.Add(job);
            return Task.FromResult(new AiJobResult("Dạ.", [], [], null) { Agent = "Codex" });
        }

        public Task UndoAsync(AiJobAction action, CancellationToken ct) => Task.CompletedTask;

        public void End(string conversationId) { }
    }

    private sealed class Fake(Func<HttpRequestMessage, Task<(HttpStatusCode Status, string Body)>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = await answer(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
