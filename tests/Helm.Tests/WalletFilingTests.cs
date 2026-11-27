using System.Text.Json;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>
/// Filing a Wallet transaction without AI: the history's sure category first (the PC's "To categorize", the phone's
/// "What was it?"), the notifications Helm could not read listed to copy, and an AI agent filing one over MCP.
/// </summary>
public sealed class WalletFilingTests : IDisposable
{
    private static readonly TimeZoneInfo Ict = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 10, 0, TimeSpan.FromHours(7));

    private readonly MemorySynced<WalletTransaction> _transactions = new();
    private readonly MemorySynced<WalletCategory> _categories = new();
    private readonly WalletStore _store;
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public WalletFilingTests()
    {
        _store = new WalletStore(_transactions, _categories, new MemorySynced<WalletBudget>(), new ManualTime(T0));
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    private string Filed(decimal amount, string description, string category, int minutesAgo = 60)
    {
        var id = _store.AddManual(amount, description, T0.AddMinutes(-minutesAgo), category);
        return id;
    }

    private string New(decimal amount, string description) =>
        _store.AddCaptured(new ParsedTransaction("Techcombank", "1903xxxx0123", amount, null, description, T0, description), autoCategorize: false).Id;

    // ---- the history ------------------------------------------------------------------------------------------

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
    public void The_history_s_sure_category_comes_first_even_when_the_suggestions_lack_it()
    {
        Filed(-30_000, "GRAB", WalletCategories.Transport);
        Filed(-30_000, "GRAB", WalletCategories.Transport);
        // Many lately in another category: the plain suggestions put it first.
        for (var i = 0; i < 5; i++) Filed(-50_000, "AN TRUA " + i, WalletCategories.Food, minutesAgo: 10 + i);
        var id = New(-35_000, "GRAB");

        var (categories, sure) = _store.SuggestSureFirst(_store.Get(id)!, id, 3);

        Assert.True(sure);
        Assert.Equal(WalletCategories.Transport, categories[0].Id);
        Assert.Equal(3, categories.Count);
        Assert.Single(categories, c => c.Id == WalletCategories.Transport);

        var unsure = New(-35_000, "SOMETHING NEW");
        var (plain, notSure) = _store.SuggestSureFirst(_store.Get(unsure)!, unsure, 3);
        Assert.False(notSure);
        Assert.Equal(_store.Suggest(_store.Get(unsure)!, 3).Select(c => c.Id), plain.Select(c => c.Id));
    }

    [Fact]
    public void To_categorize_puts_the_history_s_sure_category_first()
    {
        Filed(-30_000, "GRAB", WalletCategories.Transport);
        Filed(-30_000, "GRAB", WalletCategories.Transport);
        var id = New(-35_000, "GRAB");

        var vm = Page();

        var row = Assert.Single(vm.ToCategorize);
        Assert.Equal(WalletCategories.Transport, row.Suggestions[0].Id);
        // Only offered: nothing filed until the chip is clicked.
        Assert.Null(_store.Get(id)!.CategoryId);
    }

    // ---- notifications Helm could not read ---------------------------------------------------------------------

    private static readonly UnreadNotification Odd = new()
    {
        At = T0,
        Bank = "Techcombank",
        Title = "Thông báo",
        Text = "Quý khách vừa thanh toán 65.000 VND cho PHUC LONG COFFEE. Số dư hiện tại 1.234.567 VND.",
    };

    [Fact]
    public void The_settings_list_shows_a_notification_Helm_could_not_read_to_copy()
    {
        var capture = new WalletCapture(_store, _settings);
        capture.KeepUnread(Odd);
        var clipboard = new MemoryClipboard();
        var vm = new WalletViewModel(_store, capture, _settings, new InlineUi(), new AcceptDialogs(), clipboard, null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WalletViewModel>.Instance, Ict);

        var row = Assert.Single(vm.Unread);
        Assert.Equal("Techcombank", row.Bank);
        Assert.Equal(Odd.Text, row.Text);
        row.CopyCommand.Execute(null);
        Assert.Equal("Thông báo\n" + Odd.Text, clipboard.Text);
        Assert.Empty(_store.Transactions());
    }

    private WalletViewModel Page() =>
        new(_store, new WalletCapture(_store, _settings), _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WalletViewModel>.Instance, Ict);

    // ---- an AI agent over MCP -----------------------------------------------------------------------------------

    private McpTool Tool(string name) => new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), _store), _store).Tools.Single(t => t.Name == name);

    [Fact]
    public async Task Update_fills_in_the_category_and_description_and_Undo_takes_them_back()
    {
        var id = New(-120_000, "NGUYEN VAN NAM chuyen tien");
        var tool = Tool("wallet_update_transaction");
        Assert.NotNull(tool.Undo);

        var result = await tool.Run(Args(new { id, category = "food & drinks", description = "Ăn trưa với Nam" }), CancellationToken.None);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.Clone();
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

    private static JsonElement Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();
}
