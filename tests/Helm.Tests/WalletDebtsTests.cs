using Helm.Core.Settings;
using Helm.Modules.Wallet;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>Wallet's debt book: balances per person, repayments, due dates, and debts made from transactions.</summary>
public sealed class WalletDebtsTests : IDisposable
{
    // Vietnam time (UTC+7, no daylight saving), as on the phone.
    private static readonly TimeZoneInfo Ict = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 9, 0, 0, TimeSpan.FromHours(7));
    private readonly ManualTime _time = new(T0);
    private readonly WalletStore _wallet;
    private readonly DebtBook _book;
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public WalletDebtsTests()
    {
        _wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        _book = new DebtBook(new MemorySynced<WalletDebt>(), _wallet, _time);
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Debts_of_the_same_person_add_up_whatever_the_case_and_spacing_and_settle_at_zero()
    {
        _book.Add("Minh  Anh", 500_000, DebtEntryKind.OwesMe, "lunch");
        _time.Advance(TimeSpan.FromMinutes(1));
        _book.Add("minh anh", 200_000, DebtEntryKind.OwesMe);
        _time.Advance(TimeSpan.FromMinutes(1));
        _book.Add("Minh Anh", 100_000, DebtEntryKind.IOwe);
        Assert.Equal(600_000m, _book.Balance("MINH ANH"));

        var person = Assert.Single(_book.Open());
        Assert.Equal("Minh Anh", person.Name);
        Assert.Equal([500_000m, 700_000m, 600_000m], person.Entries.Select(e => e.BalanceAfter));
        Assert.All(_book.All(), d => Assert.Equal("Minh Anh", d.Value.Person)); // one spelling, the first one (tidied)

        // A repayment pulls toward 0 and is stored as the opposite direction.
        _time.Advance(TimeSpan.FromMinutes(1));
        var repaid = _book.Add("Minh Anh", 250_000, DebtEntryKind.Repayment);
        Assert.Equal((DebtDirection.IOwe, true), (_book.Get(repaid)!.Direction, _book.Get(repaid)!.IsRepayment));
        Assert.Equal(350_000m, _book.Balance("Minh Anh"));

        _time.Advance(TimeSpan.FromMinutes(1));
        _book.Add("Minh Anh", 350_000, DebtEntryKind.Repayment);
        Assert.Empty(_book.Open());
        var settled = Assert.Single(_book.Settled());
        Assert.Equal(5, settled.Entries.Count);
        Assert.All(_book.All(), d => Assert.Equal(T0.AddMinutes(4), d.Value.SettledAt)); // settled together

        // Nothing left to repay; a new debt starts a new account for the same person.
        Assert.Throws<InvalidOperationException>(() => _book.Add("Minh Anh", 1, DebtEntryKind.Repayment));
        _time.Advance(TimeSpan.FromMinutes(1));
        _book.Add("Minh Anh", 10_000, DebtEntryKind.IOwe);
        Assert.Equal(-10_000m, _book.Balance("Minh Anh"));
        var repay = _book.Add("Minh Anh", 10_000, DebtEntryKind.Repayment);
        Assert.Equal(DebtDirection.TheyOweMe, _book.Get(repay)!.Direction); // when I owe, my repayment adds back
        Assert.Equal(2, _book.Settled().Count);
        Assert.Throws<ArgumentException>(() => _book.Add("  ", 5, DebtEntryKind.OwesMe));
        Assert.Throws<ArgumentException>(() => _book.Add("Lan", 0, DebtEntryKind.OwesMe));
        Assert.Equal(["Minh Anh"], _book.Names());
    }

    [Fact]
    public void A_persons_due_time_is_set_and_extended_on_all_their_open_entries()
    {
        _book.Add("Lan", 100, DebtEntryKind.OwesMe);
        _book.Add("Lan", 50, DebtEntryKind.OwesMe);
        var due = new DateTimeOffset(2026, 10, 10, 18, 30, 0, TimeSpan.FromHours(7));
        Assert.Equal(2, _book.SetDue("lan", due));
        Assert.All(_book.All(), d => Assert.Equal(due, d.Value.DueAt));
        // A new entry for Lan keeps her due time.
        var later = _book.Add("Lan", 25, DebtEntryKind.OwesMe);
        Assert.Equal(due, _book.Get(later)!.DueAt);
        Assert.True(Assert.Single(_book.Open()).HasDueTime);
        _book.SetDue("Lan", null);
        Assert.All(_book.All(), d => Assert.Null(d.Value.DueAt));
    }

    [Fact]
    public void A_transaction_put_in_the_debt_book_counts_neither_as_spending_nor_income()
    {
        var lent = _wallet.AddManual(-2_000_000, "CHUYEN TIEN NAM", T0, null);
        var lunch = _wallet.AddManual(-100_000, "lunch", T0, WalletCategories.Food);
        _time.Advance(TimeSpan.FromHours(1));

        var id = _book.AddFromTransaction(lent, "Nam", DebtEntryKind.OwesMe, "rent");
        var debt = _book.Get(id)!;
        Assert.Equal((2_000_000m, DebtDirection.TheyOweMe, lent, T0), (debt.Amount, debt.Direction, debt.TransactionId, debt.CreatedAt));
        Assert.Equal(WalletCategories.Debts, _wallet.Get(lent)!.CategoryId);
        Assert.Equal("Lent to Nam", DebtFormat.ForTransaction(debt));
        Assert.Equal(id, _book.ForTransaction(lent)!.Id);
        Assert.Throws<InvalidOperationException>(() => _book.AddFromTransaction(lent, "Lan", DebtEntryKind.OwesMe));

        var month = WalletStats.Month(_wallet.Transactions(), _wallet.Categories(includeHidden: true), new DateOnly(2026, 10, 1), _time.GetUtcNow(), Ict);
        Assert.Equal(100_000m, month.Spent); // only the lunch

        // Nam pays it back in cash, then in part by transfer.
        var back = _wallet.AddManual(1_500_000, "NAM TRA", T0.AddDays(1), null);
        _book.AddFromTransaction(back, "nam", DebtEntryKind.Repayment);
        Assert.Equal("Nam paid back", DebtFormat.ForTransaction(_book.ForTransaction(back)!.Value));
        Assert.Equal(500_000m, _book.Balance("Nam"));
        Assert.Equal(0m, WalletStats.Month(_wallet.Transactions(), _wallet.Categories(includeHidden: true), new DateOnly(2026, 10, 1), _time.GetUtcNow(), Ict).Income);

        // Deleting the entry: the transaction is waiting for a category again.
        _book.Delete(id);
        Assert.Null(_wallet.Get(lent)!.CategoryId);
        Assert.Null(_book.ForTransaction(lent));
        Assert.Equal(WalletCategories.Food, _wallet.Get(lunch)!.CategoryId);
    }

    [Fact]
    public void The_debts_category_stays_out_of_the_pickers_and_suggestions()
    {
        var info = _wallet.Category(WalletCategories.Debts)!;
        Assert.Equal((CategoryKind.Transfer, true), (info.Kind, info.Hidden));
        Assert.DoesNotContain(_wallet.Categories(), c => c.Id == WalletCategories.Debts);
        var a = _wallet.AddManual(-50_000, "Nam", T0, null);
        _book.AddFromTransaction(a, "Nam", DebtEntryKind.OwesMe);
        _wallet.AddManual(-60_000, "Nam", T0, null);
        Assert.DoesNotContain(_wallet.Suggest(_wallet.Uncategorized().Single().Value, 20), c => c.Id == WalletCategories.Debts);
    }

    [Fact]
    public void The_debts_page_adds_settles_and_reports_bad_input()
    {
        var vm = new DebtsViewModel(_book, new InlineUi(), new AcceptDialogs(), NullLogger<DebtsViewModel>.Instance);
        Assert.True(vm.HasNoPeople);
        Assert.False(vm.AddCommand.CanExecute(null));

        vm.NewPerson = "An";
        vm.NewAmount = "lots";
        vm.AddCommand.Execute(null);
        Assert.True(vm.HasMessage);
        Assert.Empty(vm.People);

        vm.NewAmount = "150.000";
        vm.AddCommand.Execute(null);
        var an = Assert.Single(vm.People);
        Assert.Equal("An", an.Name);
        Assert.StartsWith("+", an.BalanceText);
        Assert.Equal("", vm.NewPerson);
        Assert.Equal(["An"], vm.PersonNames);
        vm.NewPerson = "a";
        Assert.Equal(["An"], vm.PersonSuggestions);

        // A repayment of all of it settles An: no tick box.
        vm.NewPerson = "an";
        vm.NewKindIndex = (int)DebtEntryKind.Repayment;
        vm.NewAmount = "150k";
        vm.AddCommand.Execute(null);
        Assert.Empty(vm.People);
        var settled = Assert.Single(vm.SettledPeople);
        Assert.Equal("Settled", settled.BalanceText);
        Assert.Equal(2, settled.Entries.Count);

        // A linked note or the calendar opens a person.
        vm.ShowPerson(settled.Key);
        Assert.True(settled.IsExpanded);
        Assert.True(vm.ShowSettled);
    }

    [Fact]
    public void A_transaction_row_puts_its_money_in_the_debt_book_and_opens_it()
    {
        var debts = new DebtsViewModel(_book, new InlineUi(), new AcceptDialogs(), NullLogger<DebtsViewModel>.Instance);
        _wallet.AddManual(-300_000, "CK NGUYEN VAN NAM", T0, null);
        var vm = new WalletViewModel(_wallet, new WalletCapture(_wallet, _settings), _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), null,
            NullLogger<WalletViewModel>.Instance, Ict, debts);
        Assert.True(vm.HasDebts);
        var row = vm.ToCategorize.Single();
        Assert.True(row.CanBeDebt);

        row.OpenDebtFormCommand.Execute(null);
        Assert.Equal((int)DebtEntryKind.OwesMe, row.DebtKindIndex); // money out: most often lent
        row.SaveDebtCommand.Execute(null);
        Assert.True(row.IsDebtFormOpen); // no name yet
        Assert.True(vm.HasMessage);

        row.DebtPerson = "Nam";
        row.SaveDebtCommand.Execute(null);
        Assert.False(row.IsDebtFormOpen);
        Assert.Empty(vm.ToCategorize);
        Assert.Equal(300_000m, _book.Balance("Nam"));
        var shown = vm.Days.SelectMany(d => d.Rows).Single();
        Assert.Equal(("Lent to Nam", false), (shown.DebtText, shown.CanBeDebt));

        shown.OpenDebtCommand.Execute(null);
        Assert.True(vm.IsDebtsView);
        Assert.True(Assert.Single(debts.People).IsExpanded);
        vm.ShowMoneyCommand.Execute(null);
        Assert.True(vm.IsMoneyView);
    }
}
