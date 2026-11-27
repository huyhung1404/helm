using System.Text.Json;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>
/// <c>wallet_unfiled</c>: what an agent (Fairy, H3b-4) needs to file the transactions waiting for a category, as
/// <see cref="WalletCategoryAi"/> asks it; and the filed ones whose question the phone may still show.
/// </summary>
public sealed class WalletUnfiledTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 27, 5, 0, 0, TimeSpan.Zero));
    private readonly WalletStore _wallet;

    public WalletUnfiledTests() =>
        _wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);

    private async Task<JsonElement> Unfiled(string args = "{}")
    {
        var tool = new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), _wallet, _time), _wallet).Tools.Single(t => t.Name == "wallet_unfiled");
        Assert.True(tool.ReadOnly);
        return JsonSerializer.SerializeToElement(await tool.Run(JsonDocument.Parse(args).RootElement.Clone(), default));
    }

    [Fact]
    public async Task A_waiting_transaction_comes_with_the_categories_that_fit_and_the_ones_filed_like_it()
    {
        var food = _wallet.AddCategory("Ăn uống", CategoryKind.Expense);
        _wallet.AddCategory("Đi lại", CategoryKind.Expense);
        _wallet.AddCategory("Lương", CategoryKind.Income);
        _wallet.AddManual(-45_000, "Pho bo Ly Quoc Su", _time.Now.AddDays(-3), food);
        _wallet.AddManual(-60_000, "Bun cha Huong Lien", _time.Now.AddDays(-2), food);
        var waiting = _wallet.AddManual(-50_000, "Pho ga Nam Ngu", _time.Now.AddHours(-1), null);

        var result = await Unfiled();

        Assert.Equal(1, result.GetProperty("total").GetInt32());
        var t = Assert.Single(result.GetProperty("transactions").EnumerateArray());
        Assert.Equal(waiting, t.GetProperty("id").GetString());
        Assert.Equal(-50_000m, t.GetProperty("amount").GetDecimal());
        Assert.Equal("Pho ga Nam Ngu", t.GetProperty("description").GetString());
        // the spending categories (Helm's own among them), never one for money in
        var categories = t.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("Ăn uống", categories);
        Assert.Contains("Đi lại", categories);
        Assert.DoesNotContain("Lương", categories);
        var alike = Assert.Single(t.GetProperty("alike").EnumerateArray());
        Assert.Equal("Pho bo Ly Quoc Su", alike.GetProperty("description").GetString());
        Assert.Equal("Ăn uống", alike.GetProperty("category").GetString());
    }

    [Fact]
    public async Task The_history_says_its_category_when_it_is_sure()
    {
        var coffee = _wallet.AddCategory("Cà phê", CategoryKind.Expense);
        foreach (var days in new[] { 5, 4, 3 }) _wallet.AddManual(-35_000, "Highlands Coffee", _time.Now.AddDays(-days), coffee);
        _wallet.AddManual(-35_000, "Highlands Coffee", _time.Now.AddMinutes(-5), null);

        var t = Assert.Single((await Unfiled()).GetProperty("transactions").EnumerateArray());

        Assert.Equal("Cà phê", t.GetProperty("sure").GetString());
    }

    [Fact]
    public async Task Newest_first_and_no_more_than_asked()
    {
        foreach (var hours in new[] { 3, 1, 2 }) _wallet.AddManual(-10_000 * hours, $"Khoan {hours}", _time.Now.AddHours(-hours), null);

        var result = await Unfiled("""{"limit":2}""");

        Assert.Equal(3, result.GetProperty("total").GetInt32());
        Assert.Equal(["Khoan 1", "Khoan 2"], result.GetProperty("transactions").EnumerateArray().Select(t => t.GetProperty("description").GetString()));
    }

    [Fact]
    public void The_phone_clears_the_questions_of_transactions_filed_elsewhere_lately()
    {
        var food = _wallet.AddCategory("Ăn uống", CategoryKind.Expense);
        var filed = _wallet.AddManual(-50_000, "Pho", _time.Now.AddHours(-1), null);
        var waiting = _wallet.AddManual(-20_000, "Tra da", _time.Now.AddMinutes(-30), null);
        var old = _wallet.AddManual(-90_000, "Lau", _time.Now.AddDays(-5), food);
        _wallet.SetCategory(filed, food); // Fairy filed it: the change came through the sync

        Assert.Equal([filed], _wallet.FiledSince(_time.Now.AddDays(-3)));
        Assert.DoesNotContain(waiting, _wallet.FiledSince(_time.Now.AddDays(-3)));
        Assert.DoesNotContain(old, _wallet.FiledSince(_time.Now.AddDays(-3)));
    }
}
