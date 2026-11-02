using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>Wallet's funds: what each holds, what comes off it, the warnings, carried leftovers and moved money.</summary>
public sealed class WalletFundsTests
{
    // Vietnam time (UTC+7, no daylight saving), as on the phone.
    private static readonly TimeZoneInfo Ict = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 20, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateOnly October = new(2026, 10, 1);
    private readonly ManualTime _time = new(T0);
    private readonly WalletStore _wallet;
    private readonly FundBook _funds;

    public WalletFundsTests()
    {
        _wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        _funds = new FundBook(new MemorySynced<WalletFund>(), _wallet);
    }

    private void Spend(decimal amount, string category, int day, int month = 10) =>
        _wallet.AddManual(-amount, category, new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.FromHours(7)), category);

    private FundState State(string id, DateOnly? month = null) =>
        _funds.States(_wallet.Transactions(), month ?? October, _time.GetUtcNow(), Ict).Single(s => s.Id == id);

    [Fact]
    public void Spending_in_a_funds_categories_comes_off_it_and_the_rest_is_outside_funds()
    {
        var eating = _funds.Add("Eating", 900_000, everyMonth: true, October, [WalletCategories.Food, WalletCategories.Groceries]);
        Spend(59_000, WalletCategories.Food, 24);
        Spend(214_000, WalletCategories.Groceries, 23);
        Spend(1_000_000, WalletCategories.Family, 14);
        _wallet.AddManual(-32_000, "7-Eleven", T0, categoryId: null);
        Spend(300_000, WalletCategories.Food, 30, month: 9); // another month

        var state = State(eating);
        Assert.Equal((900_000m, 273_000m, 627_000m), (state.Budget, state.Spent, state.Left));
        Assert.Equal(FundStatus.OnTrack, state.Status);

        var outside = _funds.Outside(_wallet.Transactions(), October, Ict);
        Assert.Equal(1_032_000m, outside.Spent);
        Assert.Equal([WalletCategories.Family, null], outside.CategoryIds.OrderBy(c => c is null));
    }

    [Fact]
    public void A_fund_warns_when_past_its_share_or_running_out_before_the_month_ends_and_says_how_far_over()
    {
        var eating = _funds.Add("Eating", 900_000, true, October, [WalletCategories.Food]);
        Spend(718_000, WalletCategories.Food, 10);
        // 79.8% spent, but at 718k in 24 days the 182k left lasts 6 days: it runs out on the 30th.
        var low = State(eating);
        Assert.Equal(FundStatus.Low, low.Status);
        Assert.Equal(new DateOnly(2026, 10, 30), low.RunsOutOn);

        var home = _funds.Add("Home & bills", 5_000_000, true, October, [WalletCategories.Housing], alertPercent: 90);
        Spend(5_342_000, WalletCategories.Housing, 5);
        var over = State(home);
        Assert.Equal((FundStatus.Over, -342_000m), (over.Status, over.Left));
        Assert.Equal(0, over.LeftShare);

        var fun = _funds.Add("Fun", 1_850_000, true, October, [WalletCategories.Shopping]);
        Spend(1_819_000, WalletCategories.Shopping, 15);
        Assert.Equal(FundStatus.UsedUp, State(fun).Status); // 31k left: within 2%
    }

    [Fact]
    public void A_category_belongs_to_one_fund_in_a_month()
    {
        var fun = _funds.Add("Fun & shopping", 1_850_000, true, October, [WalletCategories.Entertainment, WalletCategories.Shopping]);
        var gifts = _funds.Add("Gifts", 1_000_000, false, October, [WalletCategories.Shopping, WalletCategories.Family]);

        Assert.Equal([WalletCategories.Entertainment], _funds.Get(fun)!.Categories);
        Assert.Equal(gifts, _funds.FundOf(WalletCategories.Shopping, October)!.Id);

        // A one-month fund for September does not share a month with one that starts in October.
        var trip = _funds.Add("Trip", 2_000_000, false, October.AddMonths(-1), [WalletCategories.Entertainment]);
        Assert.Equal([WalletCategories.Entertainment], _funds.Get(fun)!.Categories);
        Assert.Equal(trip, _funds.FundOf(WalletCategories.Entertainment, October.AddMonths(-1))!.Id);
        Assert.Equal(fun, _funds.FundOf(WalletCategories.Entertainment, October)!.Id);
    }

    [Fact]
    public void One_month_funds_count_only_in_their_month_and_every_month_funds_from_their_first()
    {
        _funds.Add("Trip", 2_000_000, false, October, [WalletCategories.Travel]);
        _funds.Add("Eating", 900_000, true, October, [WalletCategories.Food]);

        Assert.Equal(["Trip", "Eating"], _funds.In(October).Select(f => f.Value.Name).OrderByDescending(n => n));
        Assert.Equal(["Eating"], _funds.In(October.AddMonths(1)).Select(f => f.Value.Name));
        Assert.Empty(_funds.In(October.AddMonths(-1)));
    }

    [Fact]
    public void A_carried_leftover_or_overspend_moves_into_the_next_month_and_moved_money_counts_once()
    {
        var september = October.AddMonths(-1);
        var health = _funds.Add("Health", 500_000, true, september, [WalletCategories.Health], carryOver: true);
        var eating = _funds.Add("Eating", 900_000, true, september, [WalletCategories.Food]);
        Spend(100_000, WalletCategories.Health, 10, month: 9);
        Spend(1_000_000, WalletCategories.Food, 10, month: 9);

        Assert.Equal((900_000m, 400_000m), (State(health).Budget, State(health).Carried)); // 400k left in September
        Assert.Equal(900_000m, State(eating).Budget); // does not carry: the overspend stays in September

        Assert.True(_funds.Move(health, eating, 342_000, October));
        Assert.Equal(558_000m, State(health).Budget);
        Assert.Equal(1_242_000m, State(eating).Budget);
        Assert.Equal(900_000m, State(eating, October.AddMonths(1)).Budget); // only for October
        Assert.False(_funds.Move(health, health, 1, October));
    }

    [Fact]
    public void Transfers_between_own_accounts_never_come_off_a_fund()
    {
        var all = _funds.Add("All", 1_000_000, true, October, [WalletCategories.Transfer, WalletCategories.Food]);
        Spend(2_000_000, WalletCategories.Transfer, 2);
        Spend(50_000, WalletCategories.Food, 3);
        Assert.Equal(50_000m, State(all).Spent);
        Assert.Equal(0m, _funds.Outside(_wallet.Transactions(), October, Ict).Spent);
    }

    [Fact]
    public void A_fund_needs_a_name_and_an_amount()
    {
        Assert.Throws<ArgumentException>(() => _funds.Add(" ", 100, true, October, []));
        Assert.Throws<ArgumentException>(() => _funds.Add("Eating", 0, true, October, []));
        var id = _funds.Add("Eating", 900_000.4m, true, October, [], alertPercent: 55);
        Assert.Equal((900_000m, 80), (_funds.Get(id)!.Amount, _funds.Get(id)!.AlertPercent));
    }
}
