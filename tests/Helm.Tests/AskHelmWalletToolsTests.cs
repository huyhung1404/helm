using System.Text.Json;
using Helm.Ai.Assistant;
using Helm.Core.Mcp;
using Helm.Modules.Tracker;
using Helm.Modules.Wallet;
using static Helm.Tests.AskHelmScript;

namespace Helm.Tests;

/// <summary>Ask Helm's money tools: a bill split among friends, a debt reminder to share, recurring bills.</summary>
public sealed class AskHelmWalletToolsTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly WalletStore _wallet;
    private readonly DebtBook _debts;
    private readonly BillBook _bills;

    public AskHelmWalletToolsTests()
    {
        _wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        _debts = new DebtBook(new MemorySynced<WalletDebt>(), _wallet, _time);
        _bills = new BillBook(new MemorySynced<WalletBill>(), _wallet, _time);
    }

    private IMcpToolProvider[] Tools => [new WalletMcpTools(_debts, _wallet), new WalletShareTools(_debts, _wallet), new BillMcpTools(_bills, _wallet)];

    private DateOnly Today => WalletFormat.LocalDay(_time.Now, TimeZoneInfo.Local);

    // ---- D1: splitting a bill ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1_200_000, 4, 300_000, 300_000)]
    [InlineData(1_000_000, 3, 333_000, 334_000)]
    [InlineData(25_000, 3, 8_333, 8_334)]
    public void A_bill_splits_evenly_and_the_users_share_takes_the_rounding(int total, int people, int each, int mine) =>
        Assert.Equal((each, mine), ((int)BillSplit.Shares(total, people).Each, (int)BillSplit.Shares(total, people).Mine));

    [Fact]
    public async Task A_split_the_user_paid_is_their_spending_plus_a_debt_per_person_each_with_its_own_Undo()
    {
        var script = new AskHelmScript(Step(Call("wallet_split_bill",
            """{"total":"1tr2","description":"Ăn lẩu","people":4,"owing":["Nam","Tú"],"paid":["Huy"],"summary":"Chia lẩu"}""")));

        var reply = await script.Say("Ăn lẩu 1tr2 chia 4, Nam với Tú chưa trả", Tools);

        // One call, one row per record, and no second call to say so.
        Assert.Equal(AssistantLoop.DoneText, reply.Text);
        Assert.Equal(["Chi 300.000đ · Ăn lẩu (phần Anh)", "Nam nợ 300.000đ · Ăn lẩu", "Tú nợ 300.000đ · Ăn lẩu", "Huy đã trả 300.000đ · Ăn lẩu"],
            reply.Done.Select(d => d.Summary));
        Assert.All(reply.Done, d => Assert.True(d.CanUndo));
        Assert.Equal(-300_000m, Assert.Single(_wallet.Transactions()).Value.Amount);
        Assert.Equal(300_000m, _debts.Balance("Nam"));
        Assert.Equal(300_000m, _debts.Balance("Tú"));
        Assert.Equal(0m, _debts.Balance("Huy"));
        Assert.Contains(_debts.Settled(), p => p.Name == "Huy");

        // Undo one row: only that record goes.
        await reply.Done[1].UndoAsync(CancellationToken.None);
        Assert.Equal(0m, _debts.Balance("Nam"));
        Assert.Equal(300_000m, _debts.Balance("Tú"));
        Assert.Single(_wallet.Transactions());
        await reply.Done[3].UndoAsync(CancellationToken.None);
        Assert.DoesNotContain(_debts.All(), d => d.Value.Person == "Huy");
        await reply.Done[0].UndoAsync(CancellationToken.None);
        Assert.Empty(_wallet.Transactions());
    }

    [Fact]
    public async Task When_someone_else_paid_the_user_owes_them_their_share()
    {
        var tools = new WalletShareTools(_debts, _wallet).Tools.Single(t => t.Name == "wallet_split_bill");

        var result = JsonSerializer.SerializeToElement(await tools.Run(Json("""{"total":"900k","description":"Cà phê","people":3,"payer":"Nam"}"""), default));

        Assert.Equal(-300_000m, _debts.Balance("Nam"));
        Assert.Empty(_wallet.Transactions());
        Assert.Equal("Anh nợ Nam 300.000đ · Cà phê", Assert.Single(result.GetProperty("records").EnumerateArray()).GetProperty("summary").GetString());
    }

    [Fact]
    public async Task A_split_with_more_people_named_than_shares_is_refused_and_saves_nothing()
    {
        var tool = new WalletShareTools(_debts, _wallet).Tools.Single(t => t.Name == "wallet_split_bill");

        var ex = await Assert.ThrowsAsync<McpToolException>(() => tool.Run(Json("""{"total":"600k","description":"Lẩu","people":2,"owing":["Nam","Tú"]}"""), default));

        Assert.Contains("split 2 ways", ex.Message);
        Assert.Empty(_wallet.Transactions());
        Assert.Empty(_debts.All());
    }

    // ---- W5: a debt reminder ------------------------------------------------------------------------------------

    [Fact]
    public void The_reminder_says_the_amount_what_for_and_since_when_politely()
    {
        _debts.Add("Nam", 200_000, DebtEntryKind.OwesMe, "Ăn lẩu");
        _time.Advance(TimeSpan.FromDays(5));
        var nam = Assert.Single(_debts.Open());

        var message = DebtReminder.Message(nam, null, _time.Now, TimeZoneInfo.Local);

        Assert.StartsWith("Chào Nam, mình nhắc nhẹ khoản 200.000đ (Ăn lẩu) từ hôm 03/11 nha.", message);
        Assert.Contains("cảm ơn Nam", message);
    }

    [Fact]
    public async Task Remind_someone_reads_the_debt_book_then_hands_the_message_to_the_share_sheet()
    {
        _debts.Add("Nam", 200_000, DebtEntryKind.OwesMe, "Ăn lẩu");
        var phone = new FakePhone();
        string? message = null;
        var script = new AskHelmScript(Step(Call("wallet_debt_reminder", """{"person":"nam"}""")));
        var assistant = script.Assistant(Tools);
        var session = assistant.Start([phone]);

        var first = await session.SendAsync("Nhắc Nam trả 200k", _ => Task.FromResult(true), CancellationToken.None);
        message = JsonDocument.Parse(script.Results[0][0].Content).RootElement.GetProperty("message").GetString();

        Assert.Contains("200.000đ", message);
        // The scope carried the share sheet with the debt book.
        Assert.Contains(script.Offered[0], t => t.Name == "phone_share_text");
        Assert.Contains(script.Offered[0], t => t.Name == "wallet_debt_reminder");
        Assert.Empty(first.Done);
        Assert.Empty(phone.Calls);
    }

    [Fact]
    public async Task Sharing_the_reminder_shows_it_in_the_card_and_Helm_sends_nothing_itself()
    {
        _debts.Add("Nam", 200_000, DebtEntryKind.OwesMe, "Ăn lẩu");
        var phone = new FakePhone();
        var script = new AskHelmScript(
            Step(Call("wallet_debt_reminder", """{"person":"Nam"}""")),
            Step(Call("phone_share_text", """{"text":"Chào Nam, mình nhắc nhẹ khoản 200.000đ nha.","summary":"Chào Nam, mình nhắc nhẹ khoản 200.000đ nha."}""")));

        var reply = await script.Say("Nhắc Nam trả 200k", Tools, [phone]);

        var (tool, args) = Assert.Single(phone.Calls);
        Assert.Equal("phone_share_text", tool);
        Assert.Equal("Chào Nam, mình nhắc nhẹ khoản 200.000đ nha.", args.GetProperty("text").GetString());
        Assert.Equal("Chào Nam, mình nhắc nhẹ khoản 200.000đ nha.", Assert.Single(reply.Done).Summary);
        Assert.Equal(200_000m, _debts.Balance("Nam"));
    }

    [Fact]
    public async Task A_reminder_for_someone_who_owes_nothing_lists_who_does()
    {
        _debts.Add("Tú", 50_000, DebtEntryKind.OwesMe);
        var tool = new WalletShareTools(_debts, _wallet).Tools.Single(t => t.Name == "wallet_debt_reminder");

        var ex = await Assert.ThrowsAsync<McpToolException>(() => tool.Run(Json("""{"person":"Nam"}"""), default));

        Assert.Contains("Tú (50.000đ)", ex.Message);
    }

    // ---- D3: recurring bills -------------------------------------------------------------------------------------

    [Fact]
    public void A_bill_counts_from_this_month_unless_its_day_has_passed()
    {
        var soon = _bills.Get(_bills.Add("Internet", 250_000, Today.Day + 2))!;
        var passed = _bills.Get(_bills.Add("Tiền nhà", 5_000_000, Today.Day - 1))!;

        Assert.Equal(Today.AddDays(2), soon.NextDue());
        Assert.Equal(new DateOnly(Today.Year, Today.Month, Today.Day - 1).AddMonths(1), passed.NextDue());
        Assert.Throws<InvalidOperationException>(() => _bills.Add("internet", null, 5));
    }

    [Fact]
    public async Task Paying_a_bill_saves_the_spending_marks_the_month_and_Undo_takes_both_back()
    {
        var id = _bills.Add("Tiền điện", null, Today.Day + 2);
        var script = new AskHelmScript(Step(Call("bill_pay", """{"bill":"điện","amount":"450k","summary":"Chi 450.000đ · Tiền điện tháng 11"}""")));

        var reply = await script.Say("đóng tiền điện rồi 450k", Tools);

        Assert.Contains(script.Offered[0], t => t.Name == "bill_pay");
        var action = Assert.Single(reply.Done);
        Assert.Equal(-450_000m, Assert.Single(_wallet.Transactions()).Value.Amount);
        var bill = _bills.Get(id)!;
        Assert.Equal(Today.AddDays(2).AddMonths(1), bill.NextDue());
        Assert.Equal(450_000m, bill.LastAmount);

        await action.UndoAsync(CancellationToken.None);
        Assert.Empty(_wallet.Transactions());
        Assert.Equal(Today.AddDays(2), _bills.Get(id)!.NextDue());
        Assert.Null(_bills.Get(id)!.LastAmount);
    }

    [Fact]
    public void A_payment_a_few_days_late_counts_for_the_month_it_was_due()
    {
        // Added on the 3rd with the 1st already past: it counts from next month; paying now is this month's, late.
        var id = _bills.Add("Nước", 120_000, 1);
        _bills.Pay(id, null);

        var bill = _bills.Get(id)!;
        Assert.Contains(new DateOnly(Today.Year, Today.Month, 1), bill.PaidMonths);
        Assert.Equal(new DateOnly(Today.Year, Today.Month, 1).AddMonths(1), bill.NextDue());
    }

    [Fact]
    public async Task A_bill_that_changes_every_month_needs_the_amount()
    {
        _bills.Add("Tiền điện", null, 10);
        var tool = new BillMcpTools(_bills, _wallet).Tools.Single(t => t.Name == "bill_pay");

        var ex = await Assert.ThrowsAsync<McpToolException>(() => tool.Run(Json("""{"bill":"tien dien"}"""), default));

        Assert.Contains("how much", ex.Message);
        Assert.Empty(_wallet.Transactions());
    }

    [Fact]
    public async Task Adding_and_listing_bills_through_the_tools()
    {
        var tools = new BillMcpTools(_bills, _wallet).Tools.ToDictionary(t => t.Name);

        var added = JsonSerializer.SerializeToElement(await tools["bill_add"].Run(Json("""{"name":"Internet","due_day":15,"amount":"250k"}"""), default));
        await tools["bill_add"].Run(Json("""{"name":"Tiền điện","due_day":20,"amount":"varies"}"""), default);
        var list = JsonSerializer.SerializeToElement(await tools["bills_list"].Run(Json("{}"), default));

        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("Internet", list[0].GetProperty("name").GetString());
        Assert.True(list[1].GetProperty("varies").GetBoolean());
        await tools["bill_add"].Undo!(added, default);
        Assert.Single(_bills.All());
    }

    [Fact]
    public void The_daily_reminder_lists_the_bills_coming_due()
    {
        var store = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
        _bills.Add("Internet", 250_000, Today.Day + 1);
        _bills.Add("Tiền nhà", 5_000_000, Math.Min(28, Today.Day + 10));

        var reminder = TrackerReminderService.Build(store, Today, daysBefore: 2, bills: _bills)!;

        Assert.Equal(1, reminder.DueSoon);
        Assert.Contains("Pay Internet (", reminder.Message);
        Assert.DoesNotContain("Tiền nhà", reminder.Message);
    }

    [Theory]
    [InlineData("Ăn lẩu 1tr2 chia 4, Nam với Tú chưa trả", "wallet")]
    [InlineData("Nhắc Nam trả 200k", "phone")]
    [InlineData("đóng tiền điện rồi 450k", "bill")]
    [InlineData("thêm hoá đơn internet 250k ngày 15 hàng tháng", "bill")]
    public void The_money_requests_carry_their_tools(string said, string group) => Assert.Contains(group, ToolScope.GroupsFor(said));

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
