using Helm.Core.Settings;
using Helm.Modules.Wallet;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public sealed class WalletTests : IDisposable
{
    // Vietnam time (UTC+7, no daylight saving), as on the phone.
    private static readonly TimeZoneInfo Ict = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 9, 0, 0, TimeSpan.FromHours(7));
    private readonly ManualTime _time = new(T0);
    private readonly MemorySynced<WalletTransaction> _transactions = new();
    private readonly MemorySynced<WalletCategory> _categories = new();
    private readonly MemorySynced<WalletBudget> _budget = new();
    private readonly WalletStore _store;
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public WalletTests()
    {
        _store = new WalletStore(_transactions, _categories, _budget, _time);
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    private static ParsedTransaction? Parse(string text, string title = "", string bank = "Techcombank", DateTimeOffset? posted = null) =>
        BankNotificationParser.Parse(bank, title, text, posted ?? T0, Ict);

    private static ParsedTransaction Parsed(decimal amount, string description, decimal? balance = null, DateTimeOffset? at = null) =>
        new("Techcombank", "1903xxxx0123", amount, balance, description, at ?? T0, description);

    // ---- Reading notifications -----------------------------------------------------------------------------------

    [Fact]
    public void Acb_sms_money_in()
    {
        var p = Parse("ACB: TK 12345678(VND) + 1,000,000 luc 10:16 01/10/2026. So du 12,345,678. GD: 123456-NGUYEN VAN A chuyen tien", "ACB", "ACB");
        Assert.NotNull(p);
        Assert.Equal(1_000_000m, p.Amount);
        Assert.Equal(12_345_678m, p.Balance);
        Assert.Equal("12345678", p.Account);
        Assert.Equal("NGUYEN VAN A chuyen tien", p.Description);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 16, 0, TimeSpan.FromHours(7)), p.OccurredAt);
    }

    [Fact]
    public void Acb_sms_money_out()
    {
        var p = Parse("ACB: TK 12345678(VND) - 55,000 luc 12:01 02/10/2026. So du 12,290,678. GD: 7891011-THANH TOAN QR GRAB", "ACB", "ACB");
        Assert.NotNull(p);
        Assert.Equal(-55_000m, p.Amount);
        Assert.Equal(12_290_678m, p.Balance);
        Assert.Equal("THANH TOAN QR GRAB", p.Description);
    }

    [Fact]
    public void Techcombank_sms_without_a_description_label()
    {
        var p = Parse("TK 1903xxxx0123 So tien GD:-150,000 So du:1,234,567 THANH TOAN QR HIGHLANDS COFFEE");
        Assert.NotNull(p);
        Assert.Equal(-150_000m, p.Amount);
        Assert.Equal(1_234_567m, p.Balance);
        Assert.Equal("1903xxxx0123", p.Account);
        Assert.Equal("THANH TOAN QR HIGHLANDS COFFEE", p.Description);
        Assert.Equal(T0, p.OccurredAt); // no time in the text: the notification's
    }

    [Fact]
    public void Techcombank_app_with_accents_on_several_lines()
    {
        var p = Parse("Tài khoản: 1903xxxx0123\nSố tiền: +5,000,000 VND\nSố dư: 12,345,678 VND\nNội dung: CONG TY ABC tra luong thang 9", "Biến động số dư");
        Assert.NotNull(p);
        Assert.Equal(5_000_000m, p.Amount);
        Assert.Equal(12_345_678m, p.Balance);
        Assert.Equal("CONG TY ABC tra luong thang 9", p.Description);
    }

    [Fact]
    public void Amount_in_the_title_dots_as_thousands()
    {
        var p = Parse("TK 1903xxxx0123 | SD: 3.456.789 VND | ND: GRABFOOD 0123", "−120.000 VND");
        Assert.NotNull(p);
        Assert.Equal(-120_000m, p.Amount);
        Assert.Equal(3_456_789m, p.Balance);
        Assert.Equal("GRABFOOD 0123", p.Description);
    }

    [Fact]
    public void Unsigned_amount_with_ghi_no()
    {
        var p = Parse("TK 1903xxxx0123 ghi nợ 200,000 VND lúc 08:15. Số dư 1,000,000 VND. ND: Rut tien ATM");
        Assert.NotNull(p);
        Assert.Equal(-200_000m, p.Amount);
        Assert.Equal("Rut tien ATM", p.Description);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 8, 15, 0, TimeSpan.FromHours(7)), p.OccurredAt);
    }

    [Fact]
    public void A_time_later_than_the_notification_was_yesterday()
    {
        var p = Parse("TK 1903xxxx0123 -50,000 VND luc 23:50 SD 900,000 VND");
        Assert.NotNull(p);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 23, 50, 0, TimeSpan.FromHours(7)), p.OccurredAt);
    }

    [Theory]
    [InlineData("Ma OTP cua Quy khach la 123456. Tuyet doi khong chia se ma nay.")]
    [InlineData("Giảm -50% cho đơn hàng từ 200,000 VND khi thanh toán bằng thẻ Techcombank!")]
    [InlineData("Quý khách có 1 tin nhắn mới")]
    [InlineData("")]
    public void Not_a_transaction(string text) => Assert.Null(Parse(text));

    [Fact]
    public void A_date_far_from_the_notification_is_not_trusted()
    {
        var p = Parse("TK 1903xxxx0123 -50,000 VND 01/01/2020 10:00 SD 900,000 VND");
        Assert.NotNull(p);
        Assert.Equal(T0, p.OccurredAt);
    }

    [Theory]
    [InlineData("vn.com.techcombank.bb.app", "Techcombank", "techcombank")]
    [InlineData("mobile.acb.com.vn", "", "acb")]
    [InlineData("com.google.android.apps.messaging", "ACB", "acb")]
    [InlineData("com.samsung.android.messaging", "Techcombank", "techcombank")]
    [InlineData("com.android.mms", "TECHCOMBANK", "techcombank")]
    [InlineData("com.zing.zalo", "ACB", null)]
    [InlineData("com.google.android.apps.messaging", "Mẹ", null)]
    [InlineData("com.google.android.apps.messaging", "ACBS Securities", null)]
    public void Identifies_the_bank(string app, string title, string? bank) =>
        Assert.Equal(bank, BankSources.Identify(app, title)?.Id);

    [Theory]
    [InlineData("50000", 50_000)]
    [InlineData("50,000", 50_000)]
    [InlineData("50.000", 50_000)]
    [InlineData("50k", 50_000)]
    [InlineData("1.5tr", 1_500_000)]
    [InlineData("2tr5", 2_500_000)]
    [InlineData("2m", 2_000_000)]
    [InlineData("120.000đ", 120_000)]
    [InlineData("1,500,000 ₫", 1_500_000)]
    public void Reads_typed_amounts(string text, decimal expected)
    {
        Assert.True(WalletText.TryParseAmount(text, out var amount));
        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    public void Rejects_typed_amounts(string text) => Assert.False(WalletText.TryParseAmount(text, out _));

    [Fact]
    public void Fold_keeps_positions()
    {
        const string text = "Số dư: Đồng";
        var folded = WalletText.Fold(text);
        Assert.Equal("so du: dong", folded);
        Assert.Equal(text.Length, folded.Length);
    }

    // ---- The store -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_same_transaction_from_the_app_and_the_sms_is_saved_once()
    {
        var app = Parse("Tài khoản: 1903xxxx0123\nSố tiền: -150,000 VND\nSố dư: 1,234,567 VND\nNội dung: THANH TOAN QR HIGHLANDS")!;
        var sms = Parse("TK 1903xxxx0123 So tien GD:-150,000 So du:1,234,567 THANH TOAN QR HIGHLANDS", posted: T0.AddMinutes(1))!;
        Assert.Equal(CaptureKind.Added, _store.AddCaptured(app, true).Kind);
        Assert.Equal(CaptureKind.Duplicate, _store.AddCaptured(sms, true).Kind);
        Assert.Single(_store.Transactions());
    }

    [Fact]
    public void Two_payments_of_the_same_amount_are_two_transactions()
    {
        _store.AddCaptured(Parsed(-30_000, "GRAB", 970_000), true);
        _store.AddCaptured(Parsed(-30_000, "GRAB", 940_000), true);
        Assert.Equal(2, _store.Transactions().Count);
    }

    [Fact]
    public void Learns_a_category_after_two_same_descriptions()
    {
        var a = _store.AddCaptured(Parsed(-30_000, "THANH TOAN QR GRAB 0912", 970_000), true);
        _store.SetCategory(a.Id, WalletCategories.Transport);
        var b = _store.AddCaptured(Parsed(-45_000, "THANH TOAN QR GRAB 1234", 925_000), true);
        Assert.Null(b.Transaction.CategoryId); // only one before: not sure yet
        _store.SetCategory(b.Id, WalletCategories.Transport);
        var c = _store.AddCaptured(Parsed(-20_000, "THANH TOAN QR GRAB 5555", 905_000), true);
        Assert.Equal(WalletCategories.Transport, c.Transaction.CategoryId);
        Assert.True(c.Transaction.AutoCategorized);
        // Turned off: left to categorize.
        var d = _store.AddCaptured(Parsed(-20_000, "THANH TOAN QR GRAB 7777", 885_000), false);
        Assert.Null(d.Transaction.CategoryId);
    }

    [Fact]
    public void Suggests_the_category_of_similar_transactions_first()
    {
        var a = _store.AddCaptured(Parsed(-60_000, "HIGHLANDS COFFEE VINCOM", 1), false);
        _store.SetCategory(a.Id, WalletCategories.Food);
        var b = _store.AddCaptured(Parsed(-500_000, "SHOPEE ORDER", 2), false);
        _store.SetCategory(b.Id, WalletCategories.Shopping);
        var c = _store.AddCaptured(Parsed(-55_000, "HIGHLANDS COFFEE LANDMARK", 3), false);
        var suggestions = _store.Suggest(c.Transaction, 3);
        Assert.Equal(WalletCategories.Food, suggestions[0].Id);
        Assert.All(suggestions, s => Assert.True(s.Fits(-1)));
        // Money in gets income categories only.
        var income = _store.Suggest(Parsed(5_000_000, "LUONG").ToTransaction(), 10);
        Assert.All(income, s => Assert.NotEqual(CategoryKind.Expense, s.Kind));
    }

    [Fact]
    public void Similar_transactions_to_categorize()
    {
        var a = _store.AddCaptured(Parsed(-60_000, "HIGHLANDS COFFEE VINCOM", 1), false);
        var b = _store.AddCaptured(Parsed(-55_000, "HIGHLANDS COFFEE VINCOM", 2), false);
        _store.AddCaptured(Parsed(-55_000, "SHOPEE", 3), false);
        _store.AddCaptured(Parsed(55_000, "HIGHLANDS COFFEE VINCOM", 4), false); // money in: not similar
        Assert.Equal([b.Id], _store.SimilarUncategorized(a.Id));
    }

    [Fact]
    public void Built_in_categories_can_be_renamed_and_hidden_and_own_ones_deleted()
    {
        Assert.True(_store.RenameCategory(WalletCategories.Food, "Ăn uống"));
        Assert.Equal("Ăn uống", _store.Category(WalletCategories.Food)!.Name);
        Assert.True(_store.SetCategoryHidden(WalletCategories.Travel, true));
        Assert.DoesNotContain(_store.Categories(), c => c.Id == WalletCategories.Travel);
        Assert.Contains(_store.Categories(includeHidden: true), c => c.Id == WalletCategories.Travel);
        Assert.False(_store.DeleteCategory(WalletCategories.Food));

        var pets = _store.AddCategory("Pets", CategoryKind.Expense);
        Assert.Throws<ArgumentException>(() => _store.AddCategory("pets", CategoryKind.Expense));
        var t = _store.AddManual(-100_000, "cat food", T0, pets);
        Assert.True(_store.DeleteCategory(pets));
        Assert.Null(_store.Get(t)!.CategoryId);
    }

    [Fact]
    public void Editing_into_the_other_direction_drops_a_category_that_no_longer_fits()
    {
        var id = _store.AddManual(-100_000, "lunch", T0, WalletCategories.Food);
        Assert.True(_store.Update(id, 100_000, "refund", T0, ""));
        Assert.Null(_store.Get(id)!.CategoryId);
    }

    // ---- Statistics ----------------------------------------------------------------------------------------------

    [Fact]
    public void Month_summary_by_category_without_transfers()
    {
        _store.AddManual(-100_000, "lunch", T0, WalletCategories.Food);
        _store.AddManual(-300_000, "dinner", T0.AddDays(-1), WalletCategories.Food);
        _store.AddManual(-100_000, "grab", T0.AddDays(-2), null);
        _store.AddManual(-2_000_000, "to savings", T0, WalletCategories.Transfer);
        _store.AddManual(10_000_000, "salary", T0.AddDays(-3), WalletCategories.Salary);
        _store.AddManual(-999_000, "last month", T0.AddDays(-10), WalletCategories.Shopping); // 24 September

        var s = WalletStats.Month(_store.Transactions(), _store.Categories(true), new DateOnly(2026, 10, 1), _time.Now, Ict);
        Assert.Equal(500_000m, s.Spent);
        Assert.Equal(10_000_000m, s.Income);
        Assert.Equal(1, s.Uncategorized);
        Assert.Equal(100_000m, s.SpentToday);
        Assert.Equal("Food & drinks", s.Spending[0].Name);
        Assert.Equal(400_000m, s.Spending[0].Amount);
        Assert.Equal(0.8, s.Spending[0].Share, 3);
        Assert.Equal(WalletStats.UncategorizedName, s.Spending[1].Name);
        Assert.Equal(999_000m, s.PreviousSpent);
        Assert.Equal(0m, s.PreviousSpentSameDay); // nothing by 4 September
    }

    [Fact]
    public void Budget_left_per_day()
    {
        _store.AddManual(-1_000_000, "rent", T0, WalletCategories.Housing);
        _store.SetMonthlyBudget(4_100_000);
        var s = WalletStats.Month(_store.Transactions(), _store.Categories(true), new DateOnly(2026, 10, 1), _time.Now, Ict);
        var b = WalletStats.Budget(_store.MonthlyBudget, s, _time.Now, Ict)!;
        Assert.Equal(3_100_000m, b.Left);
        Assert.Equal(28, b.DaysLeft); // 4 to 31 October
        Assert.Equal(110_000m, b.PerDay); // 110,714 rounded down
        Assert.Equal(24, b.Percent);
        Assert.False(b.IsOver);
        Assert.Null(WalletStats.Budget(0, s, _time.Now, Ict));
    }

    [Fact]
    public void Widget_model()
    {
        _store.AddManual(-100_000, "lunch", T0, WalletCategories.Food);
        _store.AddManual(-50_000, "grab", T0, null);
        var w = WalletWidgetModel.Build(_store, _time.Now, Ict);
        Assert.False(w.HasBudget);
        Assert.Equal(1, w.ToCategorize);
        Assert.Equal("1 to categorize", w.ToCategorizeText);
        Assert.Equal(2, w.Rows.Count);
        Assert.Equal(67, w.Rows[0].Percent);
        _store.SetMonthlyBudget(100_000);
        Assert.True(WalletWidgetModel.Build(_store, _time.Now, Ict).IsOver);
    }

    [Fact]
    public void Csv_quotes_and_defuses_formulas()
    {
        _store.AddManual(-100_000, "=HYPERLINK(\"x\")", T0, WalletCategories.Food, "a, b");
        var csv = WalletCsv.Build(_store, Ict);
        Assert.Contains("2026-10-04,09:00,-100000,Food & drinks,\"'=HYPERLINK(\"\"x\"\")\",,,,\"a, b\",manual", csv);
    }

    // ---- From notification to transaction ------------------------------------------------------------------------

    private WalletCapture Capture() => new(_store, _settings, NullLogger<WalletCapture>.Instance);

    [Fact]
    public void Capture_saves_reads_once_and_keeps_what_it_cannot_read()
    {
        var capture = Capture();
        CaptureOutcome? raised = null;
        capture.Captured += (_, c) => raised = c;
        const string sms = "ACB: TK 12345678(VND) - 55,000 luc 08:01 04/10/2026. So du 12,290,678. GD: 7891011-THANH TOAN QR GRAB";

        var r = capture.Handle("com.google.android.apps.messaging", "ACB", sms, T0, Ict);
        Assert.Equal(NotificationOutcome.Added, r.Outcome);
        Assert.NotNull(raised);
        Assert.Equal(NotificationOutcome.Duplicate, capture.Handle("com.google.android.apps.messaging", "ACB", sms, T0, Ict).Outcome);
        Assert.Equal(NotificationOutcome.Ignored, capture.Handle("com.zing.zalo", "ACB", sms, T0, Ict).Outcome);

        // From the bank, about money, but not readable: kept once.
        const string odd = "Tai khoan cua Quy khach vua bien dong so du. Mo app de xem chi tiet (VND)";
        Assert.Equal(NotificationOutcome.Unread, capture.Handle("mobile.acb.com.vn", "ACB ONE", odd + " 1", T0, Ict).Outcome);
        capture.Handle("mobile.acb.com.vn", "ACB ONE", odd + " 1", T0, Ict);
        Assert.Single(capture.Settings.Current.Unread);
        // An advert from the bank's app is not kept.
        Assert.Equal(NotificationOutcome.Ignored, capture.Handle("mobile.acb.com.vn", "ACB ONE", "Uu dai thang 10 cho chu the!", T0, Ict).Outcome);

        capture.Settings.Update(s => s.DisabledBanks.Add("acb"));
        Assert.Equal(NotificationOutcome.Ignored, capture.Handle("com.google.android.apps.messaging", "ACB", sms.Replace("55,000", "66,000"), T0, Ict).Outcome);
        capture.Settings.Update(s => s.ListenEnabled = false);
        Assert.Equal(NotificationOutcome.Ignored, capture.Handle("vn.com.techcombank.bb.app", "", "TK 1903 -1,000 SD 5,000", T0, Ict).Outcome);
    }

    [Fact]
    public void Capture_is_off_while_the_tool_is_off()
    {
        _settings.Get<GeneralSettings>(GeneralSettings.StoreId).Update(s => s.EnabledModules[WalletIds.ModuleId] = false);
        Assert.Equal(NotificationOutcome.Ignored, Capture().Handle("vn.com.techcombank.bb.app", "", "TK 1903xxxx0123 -1,000 VND SD 5,000 VND", T0, Ict).Outcome);
    }

    [Fact]
    public void Try_a_notification()
    {
        var (ok, text) = WalletCapture.Try(BankSources.Techcombank, "TK 1903xxxx0123 So tien GD:-150,000 So du:1,234,567 THANH TOAN QR", T0, Ict);
        Assert.True(ok);
        Assert.Contains("Money out", text);
        Assert.Contains("THANH TOAN QR", text);
        Assert.False(WalletCapture.Try(BankSources.Acb, "hello", T0, Ict).Ok);
    }

    [Fact]
    public void Quick_capture_spending()
    {
        var target = new SpendCaptureTarget(_store);
        Assert.True(SpendCaptureTarget.TryRead("50k coffee", out var amount, out var description));
        Assert.Equal(-50_000m, amount);
        Assert.Equal("coffee", description);
        Assert.True(SpendCaptureTarget.TryRead("+2tr bonus", out amount, out _));
        Assert.Equal(2_000_000m, amount);
        Assert.False(target.Preview("coffee").CanSave);
        Assert.True(target.Capture("35k banh mi").Saved);
        Assert.Equal(-35_000m, _store.Transactions().Single().Value.Amount);
    }

    // ---- The page ------------------------------------------------------------------------------------------------

    private WalletViewModel ViewModel() => new(_store, Capture(), _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), null,
        NullLogger<WalletViewModel>.Instance, Ict);

    [Fact]
    public void Page_categorizes_with_one_tap_and_offers_the_similar_ones()
    {
        _store.AddCaptured(Parsed(-60_000, "HIGHLANDS COFFEE VINCOM", 1, T0.AddMinutes(-2)), false);
        _store.AddCaptured(Parsed(-55_000, "HIGHLANDS COFFEE VINCOM", 2, T0.AddMinutes(-1)), false);
        var vm = ViewModel();
        Assert.Equal(2, vm.ToCategorize.Count);
        Assert.Equal("2 to categorize", vm.ToCategorizeTitle);
        var row = vm.ToCategorize[0];
        Assert.NotEmpty(row.Suggestions);
        row.Suggestions.First(s => s.Id == WalletCategories.Food).PickCommand.Execute(null);
        Assert.Single(vm.ToCategorize);
        Assert.True(vm.HasSimilar);
        vm.ApplySimilarCommand.Execute(null);
        Assert.Empty(vm.ToCategorize);
        Assert.False(vm.HasSimilar);
        Assert.Equal("115,000 ₫".Replace(",", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator), vm.SpentText);
        Assert.Equal("Food & drinks", vm.Spending.Single().Name);
    }

    [Fact]
    public void Page_adds_by_hand_and_moves_between_months()
    {
        var vm = ViewModel();
        Assert.False(vm.CanGoNext);
        vm.OpenAddCommand.Execute(null);
        vm.NewAmount = "45k";
        vm.NewDescription = "pho";
        vm.NewCategory = vm.NewCategories.First(c => c.Id == WalletCategories.Food);
        vm.AddCommand.Execute(null);
        Assert.False(vm.IsAddOpen);
        Assert.Single(vm.Days);
        Assert.Equal("Today", vm.Days[0].Title);
        Assert.Equal("pho", vm.Days[0].Rows[0].Title);

        vm.PreviousMonthCommand.Execute(null);
        Assert.Empty(vm.Days);
        Assert.True(vm.CanGoNext);
        vm.NextMonthCommand.Execute(null);
        Assert.Single(vm.Days);

        vm.FilterIndex = 2; // money in
        Assert.Empty(vm.Days);
        vm.FilterIndex = 0;
        vm.SearchText = "PHO";
        Assert.Single(vm.Days);
    }

    [Fact]
    public void Page_edits_and_saves_the_budget()
    {
        var vm = ViewModel();
        vm.MonthlyBudgetText = "10tr";
        Assert.Equal(10_000_000m, _store.MonthlyBudget);
        Assert.True(vm.HasBudget);
        vm.MonthlyBudgetText = "";
        Assert.Equal(0m, _store.MonthlyBudget);
        Assert.False(vm.HasBudget);

        _store.AddManual(-100_000, "lunch", T0, WalletCategories.Food);
        vm.Refresh();
        var row = vm.Days[0].Rows[0];
        row.BeginEditCommand.Execute(null);
        row.EditAmount = "120k";
        row.SaveEditCommand.Execute(null);
        Assert.Equal(-120_000m, _store.Get(row.Id)!.Amount);
        Assert.False(row.IsEditing);
    }
}

internal static class WalletTestExtensions
{
    public static WalletTransaction ToTransaction(this ParsedTransaction p) =>
        new() { Amount = p.Amount, Description = p.Description, OccurredAt = p.OccurredAt, Bank = p.Bank };
}
