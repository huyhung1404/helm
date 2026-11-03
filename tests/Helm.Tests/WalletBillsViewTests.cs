using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>The Wallet page's Bills section: add, edit, delete, and "Paid" saving the transaction like Ask Helm's bill_pay, each with Undo.</summary>
public sealed class WalletBillsViewTests
{
    // Noon UTC: the 3rd of November in any zone a test machine is in.
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly WalletStore _wallet;
    private readonly BillBook _bills;
    private readonly BillsViewModel _vm;

    public WalletBillsViewTests()
    {
        _wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        _bills = new BillBook(new MemorySynced<WalletBill>(), _wallet, _time);
        _vm = new BillsViewModel(_bills, new InlineUi());
    }

    private BillRow Add(string name, string amount, int day)
    {
        _vm.BeginAddCommand.Execute(null);
        _vm.FormName = name;
        _vm.FormAmount = amount;
        _vm.FormDay = day;
        _vm.SaveEditCommand.Execute(null);
        Assert.Equal("", _vm.FormProblem);
        return _vm.Rows.Single(r => r.Name == name);
    }

    [Fact]
    public void A_bill_added_on_the_page_lists_its_amount_day_and_this_month()
    {
        Assert.True(_vm.HasNoBills);
        var internet = Add("Internet", "250k", 20);
        var water = Add("Tiền nước", "", 10);

        Assert.False(_vm.IsEditing);
        Assert.Equal([water.Id, internet.Id], _vm.Rows.Select(r => r.Id));
        Assert.Equal(WalletFormat.Money(250_000), internet.AmountText);
        Assert.Equal("Due on the 20th", internet.DueText);
        Assert.Equal("Due in 17 days", internet.StateText);
        Assert.True(internet.CanPay);
        Assert.Equal("varies", water.AmountText);
        Assert.True(water.Varies);
        Assert.Equal("0 of 2 paid this month", _vm.SummaryText);
    }

    [Fact]
    public void A_bill_whose_day_has_passed_this_month_starts_next_month_but_takes_this_months_payment()
    {
        var rent = Add("Tiền nhà", "5tr", 1);

        Assert.False(rent.IsLate);
        Assert.StartsWith("From ", rent.StateText);
        Assert.Equal("", _vm.SummaryText);

        rent.PayCommand.Execute(null);
        Assert.True(rent.IsPaid);
        Assert.Contains(new DateOnly(2026, 11, 1), _bills.Get(rent.Id)!.PaidMonths);
        Assert.Equal("1 of 1 paid this month", _vm.SummaryText);
    }

    [Fact]
    public void Paid_saves_the_spending_and_marks_the_month_and_Undo_takes_both_back()
    {
        var internet = Add("Internet", "250k", 20);

        internet.PayCommand.Execute(null);

        var spent = Assert.Single(_wallet.Transactions()).Value;
        Assert.Equal(-250_000, spent.Amount);
        Assert.Equal("Internet", spent.Description);
        Assert.True(internet.IsPaid);
        Assert.Equal("Paid", internet.StateText);
        Assert.False(internet.CanPay);
        Assert.Equal($"Paid Internet · {WalletFormat.Money(250_000)}", _vm.Message);
        Assert.Equal("1 of 1 paid this month", _vm.SummaryText);

        Assert.True(_vm.CanUndo);
        _vm.UndoCommand.Execute(null);

        Assert.Empty(_wallet.Transactions());
        Assert.False(internet.IsPaid);
        Assert.Equal("", _vm.Message);
    }

    [Fact]
    public void Paid_on_a_bill_that_varies_asks_how_much_first()
    {
        var power = Add("Tiền điện", "", 15);

        power.PayCommand.Execute(null);
        Assert.True(power.IsAskingAmount);
        Assert.Empty(_wallet.Transactions());

        power.PayAmount = "abc";
        power.PayCommand.Execute(null);
        Assert.True(power.IsAskingAmount);
        Assert.StartsWith("How much was it?", _vm.Message);

        power.PayAmount = "452k";
        power.PayCommand.Execute(null);

        Assert.False(power.IsAskingAmount);
        Assert.Equal(-452_000, Assert.Single(_wallet.Transactions()).Value.Amount);
        Assert.Equal($"varies · last {WalletFormat.Money(452_000)}", power.AmountText);
    }

    [Fact]
    public void Editing_keeps_the_paid_months_and_deleting_can_be_undone()
    {
        var internet = Add("Internet", "250k", 20);
        internet.PayCommand.Execute(null);

        internet.EditCommand.Execute(null);
        Assert.Equal("Edit bill", _vm.FormTitle);
        Assert.Equal("Internet", _vm.FormName);
        Assert.Equal(20, _vm.FormDay);
        _vm.FormName = "Internet FPT";
        _vm.FormAmount = "";
        _vm.SaveEditCommand.Execute(null);

        var row = Assert.Single(_vm.Rows);
        Assert.Equal("Internet FPT", row.Name);
        Assert.True(row.Varies);
        Assert.True(row.IsPaid);

        row.DeleteCommand.Execute(null);
        Assert.Empty(_vm.Rows);
        Assert.Equal("Deleted Internet FPT", _vm.Message);
        _vm.UndoCommand.Execute(null);
        Assert.Equal("Internet FPT", Assert.Single(_vm.Rows).Name);
        Assert.True(_vm.Rows[0].IsPaid);
    }

    [Fact]
    public void The_form_says_what_is_wrong_and_keeps_what_was_typed()
    {
        Add("Internet", "250k", 20);

        _vm.BeginAddCommand.Execute(null);
        _vm.FormName = "internet";
        _vm.FormDay = 5;
        _vm.SaveEditCommand.Execute(null);
        Assert.Equal("There is already a bill Internet.", _vm.FormProblem);

        _vm.FormName = "Gym";
        _vm.FormAmount = "lots";
        _vm.SaveEditCommand.Execute(null);
        Assert.StartsWith("That is not an amount", _vm.FormProblem);

        _vm.FormAmount = "300k";
        _vm.FormDayText = "40";
        _vm.SaveEditCommand.Execute(null);
        Assert.Equal("The due day is 1 to 31.", _vm.FormProblem);
        Assert.True(_vm.IsEditing);
        Assert.Single(_vm.Rows);
    }
}
