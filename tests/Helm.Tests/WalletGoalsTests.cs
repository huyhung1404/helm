using System.Text.Json;
using Helm.Core.Settings;
using Helm.Modules.Wallet;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>Wallet's savings goals: money set aside leaves the balance without being spending, and all comes back.</summary>
public sealed class WalletGoalsTests : IDisposable
{
    // Vietnam time (UTC+7, no daylight saving), as on the phone.
    private static readonly TimeZoneInfo Ict = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 20, 0, 0, TimeSpan.FromHours(7));
    private readonly ManualTime _time = new(T0);
    private readonly WalletStore _wallet;
    private readonly GoalBook _goals;
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public WalletGoalsTests()
    {
        _wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        _goals = new GoalBook(new MemorySynced<WalletGoal>(), _wallet);
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
        // 20M in, 1.58M spent: 18.42M to spend.
        _wallet.AddManual(20_000_000, "Lương", T0.AddDays(-4), WalletCategories.Salary);
        _wallet.AddManual(-1_580_000, "Đi chợ", T0.AddDays(-2), WalletCategories.Groceries);
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    private WalletViewModel ViewModel() => new(_wallet, new WalletCapture(_wallet, _settings), _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), null,
        NullLogger<WalletViewModel>.Instance, Ict, goals: _goals);

    [Fact]
    public void Money_set_aside_leaves_the_balance_but_is_not_spending()
    {
        var pc = _goals.Add("Mini PC", 12_990_000, new DateOnly(2027, 1, 31), start: 2_000_000, Ict);
        _goals.Put(pc, 350_000, Ict);

        Assert.Equal(2_350_000m, _goals.SetAside());
        Assert.Equal(16_070_000m, _goals.Available(Ict));
        // The month's spending and the transactions do not change: nothing was spent.
        var month = WalletStats.Month(_wallet.Transactions(), _wallet.Categories(includeHidden: true), new DateOnly(2026, 11, 1), T0, Ict);
        Assert.Equal(1_580_000m, month.Spent);
        Assert.Equal(2, _wallet.Transactions().Count);
        // The widget's balance is what can be spent.
        var widget = WalletWidgetModel.Build(_wallet, WalletWidgetPeriod.Month, WalletWidgetChart.Categories, T0, Ict, _goals.SetAside());
        Assert.Equal(16_070_000m, widget.Balance);
    }

    [Fact]
    public void Never_more_than_is_missing_nor_more_than_is_available()
    {
        var keyboard = _goals.Add("Bàn phím cơ", 1_850_000, null, 1_600_000, Ict);
        var entry = _goals.Put(keyboard, 500_000, Ict);
        Assert.Equal(250_000m, entry.Amount);
        Assert.True(_goals.Get(keyboard)!.IsReady);
        Assert.Equal("100%", GoalMath.Percent(_goals.Get(keyboard)!));
        Assert.Throws<InvalidOperationException>(() => _goals.Put(keyboard, 1, Ict));

        var car = _goals.Add("Xe máy", 40_000_000, null, 0, Ict);
        var ex = Assert.Throws<InvalidOperationException>(() => _goals.Put(car, 20_000_000, Ict));
        Assert.Contains("16,570,000", ex.Message.Replace('.', ','), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => _goals.Add("Laptop", 30_000_000, null, 17_000_000, Ict));
    }

    [Fact]
    public void Taking_out_and_giving_up_bring_the_money_back_and_undo_restores_it()
    {
        var pc = _goals.Add("Mini PC", 12_990_000, null, 4_350_000, Ict);
        _goals.TakeOut(pc, 350_000);
        Assert.Equal(4_000_000m, _goals.Get(pc)!.Saved);
        Assert.Throws<InvalidOperationException>(() => _goals.TakeOut(pc, 5_000_000));

        Assert.Equal(4_000_000m, _goals.GiveUp(pc));
        Assert.Equal(0m, _goals.SetAside());
        Assert.Equal(18_420_000m, _goals.Available(Ict));
        Assert.Equal(GoalState.GaveUp, _goals.Get(pc)!.State);
        Assert.Throws<InvalidOperationException>(() => _goals.Put(pc, 100_000, Ict));

        Assert.True(_goals.Reopen(pc));
        Assert.Equal(14_420_000m, _goals.Available(Ict));
    }

    [Fact]
    public void Buying_writes_one_spending_at_the_real_price_and_the_rest_goes_back()
    {
        var pc = _goals.Add("Mini PC", 12_990_000, null, 12_990_000, Ict);
        Assert.Equal(5_430_000m, _goals.Available(Ict));

        var id = _goals.Buy(pc, 12_490_000, WalletCategories.Shopping, Ict);
        var spent = _wallet.Get(id)!;
        Assert.Equal((-12_490_000m, WalletCategories.Shopping, "Mini PC"), (spent.Amount, spent.CategoryId, spent.Description));
        // 500k cheaper: it stays in the balance.
        Assert.Equal(5_930_000m, _goals.Available(Ict));
        Assert.Equal((GoalState.Bought, 12_490_000m), (_goals.Get(pc)!.State, _goals.Get(pc)!.Paid));

        // Undo: the purchase goes, the money is set aside again.
        Assert.True(_goals.Reopen(pc));
        Assert.Null(_wallet.Get(id));
        Assert.Equal(5_430_000m, _goals.Available(Ict));

        // Dearer than saved: the extra comes off the balance, never past it.
        Assert.Throws<InvalidOperationException>(() => _goals.Buy(pc, 19_000_000, null, Ict));
        _goals.Buy(pc, 13_490_000, null, Ict);
        Assert.Equal(4_930_000m, _goals.Available(Ict));
        Assert.Throws<InvalidOperationException>(() => _goals.Delete(_goals.Add("Tai nghe", 2_000_000, null, 0, Ict)));
        Assert.True(_goals.Delete(pc));
        Assert.Equal(3, _wallet.Transactions().Count);
    }

    [Fact]
    public void The_week_share_is_what_is_missing_over_the_weeks_left()
    {
        var goal = new WalletGoal { Price = 12_990_000, Due = new DateOnly(2027, 1, 31), Entries = [new GoalEntry { Amount = 4_350_000 }] };
        // 4 Nov → 31 Jan is 89 days: 13 weeks for 8.64M.
        Assert.Equal(665_000m, GoalMath.PerWeek(goal, new DateOnly(2026, 11, 4)));
        Assert.Null(GoalMath.PerWeek(goal, new DateOnly(2027, 2, 1)));
        Assert.Null(GoalMath.PerWeek(goal with { Due = null }, new DateOnly(2026, 11, 4)));
        Assert.Equal("99%", GoalMath.Percent(goal with { Entries = [new GoalEntry { Amount = 12_980_000 }] }));
    }

    [Fact]
    public void The_page_sets_money_aside_with_quick_amounts_and_undoes_it()
    {
        var pc = _goals.Add("Mini PC", 12_990_000, null, 4_350_000, Ict);
        var vm = ViewModel();
        var goals = vm.Goals!;
        vm.ShowGoalsCommand.Execute(null);
        Assert.True(vm.IsGoalsView);
        Assert.False(vm.IsMoneyView);
        Assert.Equal("4,350,000 ₫ set aside in 1 goal · still yours", goals.SetAsideText.Replace('.', ','));

        vm.ShowGoal(pc);
        Assert.Equal(WalletPopup.Goal, vm.Popup);
        goals.AddQuickCommand.Execute("100000");
        goals.AddQuickCommand.Execute("100000");
        Assert.Equal(200_000m, decimal.Parse(goals.PutAmount.Replace(".", "").Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture));
        goals.SavePutCommand.Execute(null);
        Assert.Equal(4_550_000m, _goals.Get(pc)!.Saved);
        Assert.True(goals.HasUndo);
        Assert.Equal("35%", goals.Rows.Single().PercentText);

        goals.UndoCommand.Execute(null);
        Assert.Equal(4_350_000m, _goals.Get(pc)!.Saved);
        Assert.False(goals.HasUndo);

        // Give up from its pop-up: the money is back, the goal is under Finished.
        goals.Selected = goals.Rows.Single();
        goals.GiveUpSelectedCommand.Execute(null);
        Assert.Empty(goals.Rows);
        Assert.Single(goals.Finished);
        Assert.False(goals.HasSetAside);
    }

    [Fact]
    public void The_widget_shows_the_chosen_goal_or_the_first_one()
    {
        Assert.Null(GoalWidgetModel.Build(_goals, null, T0, Ict));
        var pc = _goals.Add("Mini PC", 12_990_000, new DateOnly(2027, 1, 31), 4_350_000, Ict);
        var keyboard = _goals.Add("Bàn phím cơ", 1_850_000, null, 1_850_000, Ict);

        var first = GoalWidgetModel.Build(_goals, null, T0, Ict)!;
        Assert.Equal((pc, "33%", false), (first.Id, first.PercentText, first.IsReady));
        Assert.StartsWith("~665K", first.PaceShort.Replace(',', '.').Replace("665.0K", "665K"), StringComparison.Ordinal);

        var chosen = GoalWidgetModel.Build(_goals, keyboard, T0, Ict)!;
        Assert.True(chosen.IsReady);
        Assert.Equal("Enough saved", chosen.LeftShort);
        _goals.Buy(keyboard, 1_850_000, null, Ict);
        Assert.Equal(pc, GoalWidgetModel.Build(_goals, keyboard, T0, Ict)!.Id);
    }

    [Fact]
    public async Task Agents_set_money_aside_by_the_goals_name_and_can_undo_it()
    {
        var pc = _goals.Add("Mini PC", 12_990_000, null, 0, Ict);
        var tools = new GoalMcpTools(_goals).Tools.ToDictionary(t => t.Name);
        var result = JsonSerializer.SerializeToElement(await tools["wallet_goal_save"].Run(Json("""{"goal":"mini pc","amount":"200k"}"""), CancellationToken.None));
        Assert.Equal(200_000m, result.GetProperty("saved").GetDecimal());
        Assert.Equal(200_000m, _goals.Get(pc)!.Saved);

        await tools["wallet_goal_save"].Undo!(result, CancellationToken.None);
        Assert.Equal(0m, _goals.Get(pc)!.Saved);

        var list = JsonSerializer.SerializeToElement(await tools["wallet_goals"].Run(Json("{}"), CancellationToken.None));
        Assert.Equal("Mini PC", list.GetProperty("goals")[0].GetProperty("name").GetString());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
