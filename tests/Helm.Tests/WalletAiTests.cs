using System.Text.Json;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>
/// Wallet's AI: filing from the history first (no AI), AI only when unsure (a PC agent; the API only when allowed),
/// a new category only as a proposal; reading a notification the parser missed, checked against its text; and
/// "what was it?" answered through Ask Helm.
/// </summary>
public sealed class WalletAiTests : IDisposable
{
    private const string ClaudeKey = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa";
    private static readonly TimeZoneInfo Ict = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 10, 0, TimeSpan.FromHours(7));

    private readonly MemorySynced<WalletTransaction> _transactions = new();
    private readonly MemorySynced<WalletCategory> _categories = new();
    private readonly WalletStore _store;
    private readonly MemorySyncedLog<AiUsageEntry> _usage = new();
    private readonly MemorySettings<AiFeatureSettings> _features = new();
    private readonly Scripted _api = new();
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public WalletAiTests()
    {
        _store = new WalletStore(_transactions, _categories, new MemorySynced<WalletBudget>(), new ManualTime(T0));
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    private AiOneShot Ai(params IAgentRoute[] pcs)
    {
        var keys = new AiKeyStore(new MemorySynced<AiKey>());
        keys.Add(AiProviders.Claude, "Helm", ClaudeKey);
        return new AiOneShot(keys, _features, new AiUsageBook(_usage), [_api], new AiRouter(pcs));
    }

    private string Filed(decimal amount, string description, string category, int minutesAgo = 60)
    {
        var id = _store.AddManual(amount, description, T0.AddMinutes(-minutesAgo), category);
        return id;
    }

    private string New(decimal amount, string description) =>
        _store.AddCaptured(new ParsedTransaction("Techcombank", "1903xxxx0123", amount, null, description, T0, description), autoCategorize: false).Id;

    /// <summary>The user allowed the API for a feature that runs by itself.</summary>
    private void AllowApi(string feature) => _features.Update(s => s.Features[feature] = new AiFeatureChoice { Route = AiRoutePolicy.PreferPc });

    // ---- W1: the history first ---------------------------------------------------------------------------------

    [Fact]
    public void Two_alike_in_one_category_make_it_sure_one_alone_does_not()
    {
        Filed(-30_000, "THANH TOAN QR GRAB 0912", WalletCategories.Transport);
        var one = _store.Guess(new WalletTransaction { Amount = -25_000, Description = "THANH TOAN QR GRAB 7777" });
        Assert.Equal(WalletCategories.Transport, one!.CategoryId);
        Assert.False(one.IsSure);

        Filed(-45_000, "QR GRAB 1234", WalletCategories.Transport);
        var two = _store.Guess(new WalletTransaction { Amount = -25_000, Description = "GRAB 5555" });
        Assert.True(two!.IsSure);
        Assert.Equal(2, two.Matches);
    }

    [Fact]
    public void Disagreeing_history_is_not_sure_and_money_in_never_votes_for_spending()
    {
        Filed(-60_000, "HIGHLANDS COFFEE", WalletCategories.Food);
        Filed(-60_000, "HIGHLANDS COFFEE", WalletCategories.Food);
        Filed(-60_000, "HIGHLANDS COFFEE", WalletCategories.Entertainment);
        Assert.False(_store.Guess(new WalletTransaction { Amount = -55_000, Description = "HIGHLANDS COFFEE" })!.IsSure);
        Assert.Null(_store.Guess(new WalletTransaction { Amount = 55_000, Description = "HIGHLANDS COFFEE" }));
    }

    [Fact]
    public void Similar_words_in_the_same_amount_band_also_count()
    {
        Filed(-8_000_000, "CHUYEN TIEN NHA THANG 9 CHU NHA TRAN VAN B", WalletCategories.Housing);
        Filed(-8_000_000, "CHUYEN TIEN NHA THANG 10 CHU NHA TRAN VAN B", WalletCategories.Housing);
        Filed(-8_000_000, "TIEN NHA THANG 8 TRAN VAN B", WalletCategories.Housing);
        var guess = _store.Guess(new WalletTransaction { Amount = -8_000_000, Description = "TIEN NHA THANG 11 TRAN VAN B CK" });
        Assert.True(guess!.IsSure);
        Assert.Equal(WalletCategories.Housing, guess.CategoryId);
    }

    [Fact]
    public async Task A_sure_history_files_it_without_asking_AI()
    {
        Filed(-30_000, "GRAB", WalletCategories.Transport);
        Filed(-30_000, "GRAB", WalletCategories.Transport);
        var id = New(-35_000, "GRAB");
        var pc = new Pc("""{"category":"Food & drinks"}""");
        AllowApi(AiFeatureIds.WalletCategorize);

        var proposal = await new WalletCategoryAi(_store, Ai(pc), Ict).ProposeAsync(id, CancellationToken.None);

        Assert.Equal(WalletCategories.Transport, proposal!.CategoryId);
        Assert.False(proposal.FromAi);
        Assert.Empty(pc.Jobs);
        Assert.Equal(0, _api.Calls);
        Assert.Empty(_usage.All());
    }

    // ---- W1: AI when unsure -------------------------------------------------------------------------------------

    [Fact]
    public async Task Unsure_it_asks_a_PC_agent_with_the_categories_and_transactions_like_it()
    {
        Filed(-120_000, "PHUC LONG CAFE", WalletCategories.Food);
        var id = New(-65_000, "PHUC LONG Q1");
        var pc = new Pc("""{"category":"food & DRINKS"}""");

        var proposal = await new WalletCategoryAi(_store, Ai(pc), Ict).ProposeAsync(id, CancellationToken.None);

        Assert.Equal(WalletCategories.Food, proposal!.CategoryId);
        Assert.True(proposal.FromAi);
        var job = Assert.Single(pc.Jobs);
        Assert.Equal(AiFeatureIds.WalletCategorize, job.Feature);
        Assert.Empty(job.Tools);
        Assert.Equal(WalletCategoryAi.System, job.System);
        Assert.Contains("Food & drinks", job.Text);
        Assert.Contains("“PHUC LONG CAFE” → Food & drinks", job.Text);
        Assert.Contains("“PHUC LONG Q1”", job.Text);
        Assert.DoesNotContain("Salary", job.Text); // spending: income categories are not offered
        Assert.Equal(AiUsageBook.AgentRoute, Assert.Single(_usage.All()).Value.Route);
        // A proposal is a suggestion: nothing was filed.
        Assert.Null(_store.Get(id)!.CategoryId);
    }

    [Fact]
    public async Task By_itself_it_never_spends_on_the_API_unless_the_user_allowed_it()
    {
        var id = New(-65_000, "SOMETHING NEW");
        _api.Answer = """{"category":"Shopping"}""";

        Assert.Null(await new WalletCategoryAi(_store, Ai(new Pc(null)), Ict).ProposeAsync(id, CancellationToken.None));
        Assert.Equal(0, _api.Calls);

        AllowApi(AiFeatureIds.WalletCategorize);
        var proposal = await new WalletCategoryAi(_store, Ai(new Pc(null)), Ict).ProposeAsync(id, CancellationToken.None);
        Assert.Equal(WalletCategories.Shopping, proposal!.CategoryId);
        Assert.Equal(1, _api.Calls);
        var entry = Assert.Single(_usage.All()).Value;
        Assert.Equal(AiFeatureIds.WalletCategorize, entry.Feature);
        Assert.Equal(AiUsageBook.ApiRoute, entry.Route);
    }

    [Fact]
    public async Task A_new_category_is_only_proposed_and_one_it_already_has_is_used()
    {
        var id = New(-45_000, "PET SHOP MEO MUOP");
        var proposal = await new WalletCategoryAi(_store, Ai(new Pc("```json\n{\"new_category\":\"Thú cưng\"}\n```")), Ict).ProposeAsync(id, CancellationToken.None);

        Assert.True(proposal!.IsNew);
        Assert.Equal("Thú cưng", proposal.NewName);
        Assert.Equal(CategoryKind.Expense, proposal.NewKind);
        Assert.DoesNotContain(_store.Categories(), c => c.Name == "Thú cưng");

        // "New" but already there (folded alike): that one.
        var existing = await new WalletCategoryAi(_store, Ai(new Pc("""{"new_category":"GROCERIES"}""")), Ict).ProposeAsync(id, CancellationToken.None);
        Assert.Equal(WalletCategories.Groceries, existing!.CategoryId);
        Assert.False(existing.IsNew);
    }

    [Fact]
    public void The_notification_puts_AI_s_answer_first_and_a_new_category_as_a_plus()
    {
        // No time yet (as the "went out without a notification" reminder asks): suggestions still come.
        var suggestions = _store.Suggest(new WalletTransaction { Amount = -45_000, Description = "X" }, 3);
        Assert.Equal(3, suggestions.Count);
        var picked = CategoryButtons.For(suggestions, new CategoryProposal(WalletCategories.Travel, null, CategoryKind.Expense, true), _store.Category, 2);
        Assert.Equal([WalletCategories.Travel, suggestions[0].Id], picked.Select(b => b.CategoryId));

        var proposed = CategoryButtons.For(suggestions, new CategoryProposal(null, "Thú cưng", CategoryKind.Expense, true), _store.Category, 2);
        Assert.Equal("+ Thú cưng", proposed[0].Label);
        Assert.Null(proposed[0].CategoryId);
        Assert.Equal(suggestions[0].Id, proposed[1].CategoryId);

        Assert.Equal(suggestions.Take(2).Select(s => s.Id), CategoryButtons.For(suggestions, null, _store.Category, 2).Select(b => b.CategoryId));
    }

    [Theory]
    [InlineData("""{"category":"Salary"}""")] // money in, for a payment
    [InlineData("""{"category":"Made up"}""")]
    [InlineData("""{"category":null}""")]
    [InlineData("I think it is food.")]
    [InlineData("""{"new_category":""}""")]
    public async Task An_answer_that_does_not_hold_up_is_no_proposal(string answer)
    {
        var id = New(-45_000, "UNKNOWN SHOP");
        Assert.Null(await new WalletCategoryAi(_store, Ai(new Pc(answer)), Ict).ProposeAsync(id, CancellationToken.None));
    }

    // ---- W2: reading a notification the parser missed ------------------------------------------------------

    private static readonly UnreadNotification Odd = new()
    {
        At = T0,
        Bank = "Techcombank",
        Title = "Thông báo",
        Text = "Quý khách vừa thanh toán 65.000 VND cho PHUC LONG COFFEE. Số dư hiện tại 1.234.567 VND.",
    };

    [Fact]
    public void An_extraction_in_the_text_becomes_a_transaction()
    {
        var parsed = WalletNotificationAi.Extract(
            """{"amount":65000,"direction":"out","counterpart":"PHUC LONG COFFEE","description":null,"balance":"1.234.567","account":"9999"}""", Odd);

        Assert.NotNull(parsed);
        Assert.Equal(-65_000m, parsed.Amount);
        Assert.Equal(1_234_567m, parsed.Balance);
        Assert.Equal("", parsed.Account); // not in the text: dropped
        Assert.Equal("PHUC LONG COFFEE", parsed.Description);
        Assert.Equal(T0, parsed.OccurredAt);
        Assert.Contains("65.000 VND", parsed.Original);
    }

    [Theory]
    [InlineData("""{"amount":56000,"direction":"out"}""")] // not in the text
    [InlineData("""{"amount":65000,"direction":"sideways"}""")]
    [InlineData("""{"amount":0,"direction":"out"}""")]
    [InlineData("""{"transaction":null}""")]
    [InlineData("no json here")]
    public void An_extraction_the_text_does_not_back_is_refused(string answer) =>
        Assert.Null(WalletNotificationAi.Extract(answer, Odd));

    [Fact]
    public void A_direction_against_the_sign_in_the_text_is_refused_and_a_balance_not_in_it_dropped()
    {
        var signed = Odd with { Text = "TK 1903xxxx0123 GD: +2,000,000 luc 10:00. SD 9,000,000" };
        Assert.Null(WalletNotificationAi.Extract("""{"amount":2000000,"direction":"out"}""", signed));
        var parsed = WalletNotificationAi.Extract("""{"amount":2000000,"direction":"in","balance":8000000,"account":"1903xxxx0123"}""", signed);
        Assert.Equal(2_000_000m, parsed!.Amount);
        Assert.Null(parsed.Balance);
        Assert.Equal("1903xxxx0123", parsed.Account);
    }

    [Fact]
    public async Task Read_with_AI_saves_it_from_the_list_and_Undo_puts_it_back()
    {
        var capture = new WalletCapture(_store, _settings);
        capture.KeepUnread(Odd);
        _api.Answer = """{"amount":65000,"direction":"out","counterpart":"PHUC LONG COFFEE","balance":1234567}""";
        var reader = new WalletNotificationAi(_store, capture, Ai());

        var read = await reader.ReadAsync(capture.Settings.Current.Unread[0], CancellationToken.None);

        Assert.True(read.Ok, read.Problem);
        Assert.Equal(-65_000m, _store.Get(read.TransactionId!)!.Amount);
        Assert.Empty(capture.Settings.Current.Unread);
        // Started by the user: the API answered (no PC), counted under its own feature.
        Assert.Equal(AiFeatureIds.WalletReadNotification, Assert.Single(_usage.All()).Value.Feature);
        Assert.Contains("PHUC LONG COFFEE", _api.LastUser);

        reader.Undo(read, Odd);
        Assert.Empty(_store.Transactions());
        Assert.Single(capture.Settings.Current.Unread);
    }

    [Fact]
    public async Task A_one_time_code_is_never_sent()
    {
        var capture = new WalletCapture(_store, _settings);
        var otp = Odd with { Text = "Ma OTP cua Quy khach la 482913. So tien 5,000,000 VND" };
        var read = await new WalletNotificationAi(_store, capture, Ai()).ReadAsync(otp, CancellationToken.None);
        Assert.False(read.Ok);
        Assert.Equal(0, _api.Calls);
    }

    // ---- The PC's "to categorize" list ---------------------------------------------------------------------------

    private WalletViewModel Page(string answer) =>
        new(_store, new WalletCapture(_store, _settings), _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WalletViewModel>.Instance, Ict,
            categoryAi: new WalletCategoryAi(_store, Ai(new Pc(answer)), Ict));

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(20);
        Assert.True(done());
    }

    [Fact]
    public async Task To_categorize_puts_AIs_pick_first_when_the_list_opens()
    {
        var id = New(-65_000, "SOMETHING NEW");
        var vm = Page("""{"category":"Shopping"}""");
        var row = Assert.Single(vm.ToCategorize);
        Assert.NotEqual(WalletCategories.Shopping, row.Suggestions.FirstOrDefault()?.Id);

        vm.OpenToCategorizeCommand.Execute(null);

        await Until(() => vm.ToCategorize.Single().Suggestions.FirstOrDefault()?.Id == WalletCategories.Shopping);
        Assert.False(vm.ToCategorize.Single().HasProposedCategory);
        // Only proposed: nothing filed until the chip is clicked.
        Assert.Null(_store.Get(id)!.CategoryId);
    }

    [Fact]
    public async Task To_categorize_offers_AIs_new_category_and_makes_it_only_when_clicked()
    {
        var id = New(-45_000, "PET SHOP MEO MUOP");
        var vm = Page("""{"new_category":"Thú cưng"}""");

        vm.OpenToCategorizeCommand.Execute(null);
        await Until(() => vm.ToCategorize.Single().HasProposedCategory);

        var row = vm.ToCategorize.Single();
        Assert.Equal("+ Thú cưng", row.ProposedCategoryText);
        Assert.DoesNotContain(_store.Categories(), c => c.Name == "Thú cưng");

        row.AcceptProposedCategoryCommand.Execute(null);

        Assert.Equal("Thú cưng", _store.Category(_store.Get(id)!.CategoryId!)!.Name);
        Assert.Empty(vm.ToCategorize);
    }

    [Fact]
    public async Task The_settings_list_reads_one_and_offers_Undo_in_its_place()
    {
        var capture = new WalletCapture(_store, _settings);
        capture.KeepUnread(Odd);
        _api.Answer = """{"amount":65000,"direction":"out","counterpart":"PHUC LONG COFFEE"}""";
        var vm = new WalletViewModel(_store, capture, _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WalletViewModel>.Instance, Ict, notificationAi: new WalletNotificationAi(_store, capture, Ai()));

        var row = Assert.Single(vm.Unread);
        Assert.Equal("Read with AI", row.ActionText);
        await row.ActionCommand.ExecuteAsync(null);

        var done = Assert.Single(vm.Unread);
        Assert.Equal("Undo", done.ActionText);
        Assert.StartsWith("Saved: ", done.Text);
        Assert.Single(_store.Transactions());
        await done.ActionCommand.ExecuteAsync(null);
        Assert.Empty(_store.Transactions());
        Assert.Equal("Read with AI", Assert.Single(vm.Unread).ActionText);
    }

    // ---- W6: "what was it?" by voice ------------------------------------------------------------------------

    private McpTool Tool(string name) => new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), _store), _store).Tools.Single(t => t.Name == name);

    [Fact]
    public async Task Update_fills_in_the_category_and_description_and_Undo_takes_them_back()
    {
        var id = New(-120_000, "NGUYEN VAN NAM chuyen tien");
        var tool = Tool("wallet_update_transaction");
        Assert.NotNull(tool.Undo);

        var result = await tool.Run(Args(new { id, category = "food & drinks", description = "Ăn trưa với Nam" }), CancellationToken.None);
        var json = JsonDocument.Parse(HelmAssistant.Serialize(result)).RootElement.Clone();
        var updated = _store.Get(id)!;
        Assert.Equal(WalletCategories.Food, updated.CategoryId);
        Assert.Equal("Ăn trưa với Nam", updated.Description);

        await tool.Undo!(json, CancellationToken.None);
        var back = _store.Get(id)!;
        Assert.Null(back.CategoryId);
        Assert.Equal("NGUYEN VAN NAM chuyen tien", back.Description);
    }

    [Fact]
    public async Task Update_refuses_an_unknown_id_a_category_of_the_other_direction_and_nothing_to_change()
    {
        var id = New(-120_000, "X");
        var tool = Tool("wallet_update_transaction");
        await Assert.ThrowsAsync<McpToolException>(() => tool.Run(Args(new { id = "nope", category = "Food & drinks" }), CancellationToken.None));
        await Assert.ThrowsAsync<McpToolException>(() => tool.Run(Args(new { id, category = "Salary" }), CancellationToken.None));
        await Assert.ThrowsAsync<McpToolException>(() => tool.Run(Args(new { id }), CancellationToken.None));
        Assert.Null(_store.Get(id)!.CategoryId);
    }

    [Fact]
    public async Task Ask_Helm_opened_about_a_transaction_carries_it_and_only_the_wallet_tools()
    {
        var id = New(-120_000, "NGUYEN VAN NAM chuyen tien");
        var keys = new AiKeyStore(new MemorySynced<AiKey>());
        keys.Add(AiProviders.Claude, "Helm", ClaudeKey);
        var call = new ToolCall("1", "wallet_update_transaction", JsonDocument.Parse(
            $$"""{"id":"{{id}}","category":"Food & drinks","description":"Ăn trưa với Nam","summary":"Ăn trưa với Nam · Ăn uống"}""").RootElement.Clone());
        _api.Steps.Enqueue(new AssistantStep("", [call], new AssistantUsage(100, 10, 0, 0), false));
        var assistant = new HelmAssistant(keys, _features, new AiUsageBook(_usage),
            [new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), _store), _store), new OtherTool()], [_api], _ => true);
        var context = WalletAskContext.For(id, _store.Get(id)!, Ict);
        Assert.Contains("khoản này là gì?", context.Title);

        // Words that name no tool group: the context's group still comes.
        var reply = await assistant.Start(context: context).SendAsync("cùng Nam", _ => Task.FromResult(true), CancellationToken.None);

        Assert.Null(reply.Problem);
        Assert.Contains(id, _api.LastUser);
        Assert.Contains("The user says: cùng Nam", _api.LastUser);
        Assert.Contains(_api.LastTools, t => t == "wallet_update_transaction");
        Assert.DoesNotContain(_api.LastTools, t => t.StartsWith("tracker_", StringComparison.Ordinal));
        Assert.Equal(WalletCategories.Food, _store.Get(id)!.CategoryId);
        await Assert.Single(reply.Done).UndoAsync(CancellationToken.None);
        Assert.Null(_store.Get(id)!.CategoryId);
    }

    private static JsonElement Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    // ---- doubles ----------------------------------------------------------------------------------------------

    /// <summary>A Helm PC whose agent answers with <c>answer</c>; none: it does not take the job in time.</summary>
    private sealed class Pc(string? answer) : IAgentRoute
    {
        public List<AiJob> Jobs { get; } = [];
        public string Device => "PC-HUNG";
        public string? Unavailable() => null;

        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            Jobs.Add(job);
            return answer is null ? throw new AiRouteException("timed out") : Task.FromResult(new AiJobResult(answer, [], [], 0.001m) { Agent = "Claude Code" });
        }

        public Task UndoAsync(AiJobAction action, CancellationToken ct) => Task.CompletedTask;

        public void End(string conversationId) { }
    }

    /// <summary>The API: answers the queued steps, then <see cref="Answer"/>.</summary>
    private sealed class Scripted : IAssistantChatFactory
    {
        public int Calls { get; private set; }
        public string Answer { get; set; } = "";
        public string LastUser { get; private set; } = "";
        public IReadOnlyList<string> LastTools { get; private set; } = [];
        public Queue<AssistantStep> Steps { get; } = new();
        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            LastTools = [.. tools.Select(t => t.Name)];
            return new Chat(this);
        }

        private sealed class Chat(Scripted api) : IAssistantChat
        {
            public void AddUser(string text) => api.LastUser = text;

            public Task<AssistantStep> NextAsync(CancellationToken ct)
            {
                api.Calls++;
                return Task.FromResult(api.Steps.Count > 0 ? api.Steps.Dequeue() : new AssistantStep(api.Answer, [], new AssistantUsage(100, 10, 0, 0), false));
            }

            public void AddResults(IReadOnlyList<ToolOutcome> results) { }
        }
    }

    private sealed class OtherTool : IMcpToolProvider
    {
        public string? ModuleId => null;

        public IEnumerable<McpTool> Tools =>
            [new("tracker_add_task", "Adds a task.", McpTool.NoArguments(), (_, _) => Task.FromResult<object?>("ok")) { Undo = (_, _) => Task.CompletedTask }];
    }
}
