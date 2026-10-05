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

    [Theory]
    [InlineData("Ma OTP 482913 de chuyen tien so tien 5,000,000 VND den TK 0123456789. Khong chia se ma nay.")]
    [InlineData("Ma xac thuc giao dich: 771204. So tien: 2,000,000 VND. Hieu luc 3 phut.")]
    [InlineData("482913 la ma OTP cua Quy khach. Tuyet doi khong cung cap cho bat ky ai.")]
    [InlineData("Mã xác nhận của bạn là 4821")]
    [InlineData("SmartOTP: 556677 cho giao dich 1,500,000 VND")]
    [InlineData("Your OTP is 123456")]
    public void A_one_time_code_is_recognised(string text) => Assert.True(BankNotificationParser.HasOneTimeCode("", text));

    [Theory]
    [InlineData("TK 1903xxxx0123 -50,000 VND luc 08:00 04/10/2026 SD 900,000 VND ND: THANH TOAN QR")]
    // The warning some banks add to every message has no code in it.
    [InlineData("TK 1903xxxx0123 -50,000 VND luc 08:00 SD 900,000 VND. Techcombank khong bao gio yeu cau cung cap OTP.")]
    [InlineData("So du 5000000. Khong chia se OTP voi bat ky ai")]
    [InlineData("TK 12345678 +2,000,000 SD 9,000,000 Ma GD 87654321 ND: luong thang 10")]
    public void A_transaction_is_not_a_one_time_code(string text) => Assert.False(BankNotificationParser.HasOneTimeCode("", text));

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
    // A contact named after the bank is not the bank.
    [InlineData("com.google.android.apps.messaging", "ACB Nam", null)]
    [InlineData("com.google.android.apps.messaging", "TCB: Lan", null)]
    // Any app can choose a package name that looks like a bank's or an SMS app's.
    [InlineData("com.evil.techcombank.fake", "Techcombank", null)]
    [InlineData("Vn.com.techcombank.bb.app", "Techcombank", null)]
    [InlineData("com.game.acb", "ACB", null)]
    [InlineData("com.fake.sms", "Techcombank", null)]
    [InlineData("com.example.messages", "TCB", null)]
    [InlineData("org.telegram.messenger", "TCB", null)]
    public void Identifies_the_bank(string app, string title, string? bank) =>
        Assert.Equal(bank, BankSources.Identify(app, title)?.Id);

    [Fact]
    public void The_default_SMS_app_counts_as_an_SMS_app()
    {
        Assert.Null(BankSources.Identify("com.textra", "Techcombank"));
        Assert.Equal("techcombank", BankSources.Identify("com.textra", "Techcombank", smsApp: "com.textra")?.Id);
        Assert.Null(BankSources.Identify("com.fake.sms", "Techcombank", smsApp: "com.textra"));
    }

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
    public void Widget_balance_is_everything_received_minus_everything_spent()
    {
        // The banks' own balances are ignored: cash taken out is still the user's money.
        _store.AddCaptured(Parsed(-100_000, "lunch", 900_000, T0.AddHours(-2)), false);
        _store.AddCaptured(Parsed(-50_000, "coffee", 850_000, T0), false);
        _store.AddCaptured(new ParsedTransaction("ACB", "12345678", 2_000_000, 5_000_000, "salary", T0.AddDays(-5), "salary"), false);
        _store.AddManual(-300_000, "rent", T0.AddDays(-10), WalletCategories.Housing);
        _store.AddManual(-1_000_000, "to savings", T0, WalletCategories.Transfer); // neither spending nor income

        var w = WalletWidgetModel.Build(_store, WalletWidgetPeriod.Week, WalletWidgetChart.Categories, _time.Now, Ict);
        Assert.Equal(1_550_000m, w.Balance); // 2M in, 450K out
        Assert.Equal("Balance", WalletWidgetModel.CenterLabel);
        Assert.Equal("1,550,000 ₫", w.BalanceText.Replace('.', ','));
        Assert.Equal(150_000m, w.Spent);
        Assert.Equal(2_000_000m, w.Earned); // Tuesday 29 September: this week (from Monday), not this month
        Assert.Equal("−150K · +2M", w.CenterDetail);
        Assert.Equal("3", w.BadgeText); // the three captured transactions wait for a category

        var month = WalletWidgetModel.Build(_store, WalletWidgetPeriod.Month, WalletWidgetChart.Categories, _time.Now, Ict);
        Assert.Equal(0m, month.Earned);
        Assert.Equal(1_550_000m, month.Balance); // the same whatever the tab
        var all = WalletWidgetModel.Build(_store, WalletWidgetPeriod.All, WalletWidgetChart.Categories, _time.Now, Ict);
        Assert.Equal(450_000m, all.Spent);
        Assert.Equal("Housing", all.Slices[0].Name);
        Assert.Equal(WalletStats.UncategorizedName, all.Slices[1].Name);
        Assert.Equal(0, all.Slices[1].Color); // the neutral grey

        _store.AddManual(-2_000_000, "phone", T0, WalletCategories.Shopping);
        w = WalletWidgetModel.Build(_store, WalletWidgetPeriod.Today, WalletWidgetChart.Categories, _time.Now, Ict);
        Assert.Equal(-450_000m, w.Balance);
        Assert.StartsWith("−", w.BalanceText);
    }

    [Fact]
    public void Widget_balance_ring()
    {
        _store.AddManual(1_000_000, "salary", T0.AddDays(-20), WalletCategories.Salary);
        _store.AddManual(-250_000, "lunch", T0, WalletCategories.Food);
        var w = WalletWidgetModel.Build(_store, WalletWidgetPeriod.Today, WalletWidgetChart.BalanceAndSpent, _time.Now, Ict);
        Assert.Equal(750_000m, w.Balance);
        Assert.Equal(2, w.Slices.Count);
        Assert.Equal(250_000m, w.Slices[0].Amount);
        Assert.Equal(WalletWidgetModel.SpentColor, w.Slices[0].Color);
        Assert.Equal(WalletWidgetModel.BalanceColor, w.Slices[1].Color);
        Assert.Equal(0.25, w.Slices[0].Share, 3); // 250K of 750K + 250K
        Assert.Null(w.BadgeText);

        // More spent than received: no balance slice, the spending fills the ring.
        _store.AddManual(-2_000_000, "phone", T0, WalletCategories.Shopping);
        w = WalletWidgetModel.Build(_store, WalletWidgetPeriod.Today, WalletWidgetChart.BalanceAndSpent, _time.Now, Ict);
        Assert.Equal(WalletWidgetModel.SpentColor, Assert.Single(w.Slices).Color);
    }

    [Fact]
    public void Widget_folds_small_categories_and_counts_the_waiting_ones()
    {
        foreach (var (category, amount) in new[]
                 {
                     (WalletCategories.Food, 600_000), (WalletCategories.Groceries, 500_000), (WalletCategories.Shopping, 400_000),
                     (WalletCategories.Bills, 300_000), (WalletCategories.Transport, 200_000), (WalletCategories.Health, 100_000),
                 })
            _store.AddManual(-amount, category, T0, category);
        var w = WalletWidgetModel.Build(_store, WalletWidgetPeriod.Month, WalletWidgetChart.Categories, _time.Now, Ict);
        Assert.Equal(WalletWidgetModel.MaxSlices, w.Slices.Count);
        Assert.Equal("Other (2)", w.Slices[^1].Name);
        Assert.Equal(300_000m, w.Slices[^1].Amount);
        Assert.Equal(WalletWidgetModel.OtherColor, w.Slices[^1].Color);
        Assert.Equal(1.0, w.Slices.Sum(s => s.Share), 6);

        for (var i = 0; i < 120; i++) _store.AddManual(-1_000, "?", T0, null);
        Assert.Equal("99+", WalletWidgetModel.Build(_store, WalletWidgetPeriod.Month, WalletWidgetChart.Categories, _time.Now, Ict).BadgeText);
    }

    [Theory]
    [InlineData("2026-10-04", WalletWidgetPeriod.Week, "2026-09-28")] // Sunday: since Monday
    [InlineData("2026-10-05", WalletWidgetPeriod.Week, "2026-10-05")] // Monday: only today
    [InlineData("2026-10-04", WalletWidgetPeriod.Month, "2026-10-01")]
    [InlineData("2026-10-04", WalletWidgetPeriod.Today, "2026-10-04")]
    public void Widget_periods(string today, WalletWidgetPeriod period, string from)
    {
        var day = DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal((DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), day), WalletWidgetModel.Range(period, day));
        Assert.Null(WalletWidgetModel.Range(WalletWidgetPeriod.All, day).From);
        Assert.Equal(WalletWidgetPeriod.Today, WalletWidgetModel.Next(WalletWidgetPeriod.All));
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
    public void A_one_time_code_is_never_kept()
    {
        var capture = Capture();
        Assert.Equal(NotificationOutcome.Ignored, capture.Handle("vn.com.techcombank.bb.app", "Techcombank",
            "Ma OTP 482913 de chuyen tien so tien 5,000,000 VND den TK 0123456789.", T0, Ict).Outcome);
        Assert.Equal(NotificationOutcome.Ignored, capture.Handle("com.google.android.apps.messaging", "TCB",
            "Ma xac thuc giao dich: 771204. So tien: 2,000,000 VND.", T0, Ict).Outcome);
        Assert.Empty(capture.Settings.Current.Unread);
        Assert.Empty(_store.Transactions());
        Assert.False(WalletCapture.Try(BankSources.Techcombank, "OTP: 482913, so tien 5,000,000 VND", T0, Ict).Ok);

        // A transaction that ends with the bank's warning about OTPs is still saved.
        Assert.Equal(NotificationOutcome.Added, capture.Handle("vn.com.techcombank.bb.app", "Techcombank",
            "TK 1903xxxx0123 -50,000 VND luc 08:00 SD 900,000 VND. Techcombank khong bao gio yeu cau cung cap OTP.", T0, Ict).Outcome);
    }

    [Fact]
    public void One_time_codes_kept_by_an_older_version_are_forgotten()
    {
        _settings.Get<WalletSettings>(WalletIds.ModuleId).Update(s =>
        {
            s.Unread.Add(new UnreadNotification { Bank = "Techcombank", Text = "Ma OTP 482913 de chuyen tien so tien 5,000,000 VND" });
            s.Unread.Add(new UnreadNotification { Bank = "ACB", Text = "Tai khoan vua bien dong so du (VND) 1" });
        });
        var unread = Assert.Single(Capture().Settings.Current.Unread);
        Assert.Equal("ACB", unread.Bank);
    }

    [Fact]
    public void Another_app_cannot_add_transactions()
    {
        var capture = Capture();
        const string fake = "TK 1903xxxx0123 -9,999,000 VND luc 08:00 SD 1,000 VND ND: TEST";
        Assert.Equal(NotificationOutcome.Ignored, capture.Handle("com.evil.techcombank.fake", "Techcombank", fake, T0, Ict).Outcome);
        Assert.Equal(NotificationOutcome.Ignored, capture.Handle("com.fake.sms", "Techcombank", fake, T0, Ict).Outcome);
        Assert.Equal(NotificationOutcome.Ignored, capture.Handle("com.google.android.apps.messaging", "ACB Nam", fake, T0, Ict).Outcome);
        Assert.Empty(_store.Transactions());
        Assert.Empty(capture.Settings.Current.Unread);
        // The phone's default SMS app is trusted like the common ones.
        Assert.Equal(NotificationOutcome.Added, capture.Handle("com.textra", "Techcombank", fake, T0, Ict, smsApp: "com.textra").Outcome);
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
        Assert.Equal("Food & drinks", vm.SpendingDonut.Segments.Single().Name);
        Assert.Equal("Food", vm.SpendingDonut.Segments.Single().Icon);
        Assert.True(vm.HasDailyChart);
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

    [Theory]
    [InlineData("14:30", 14, 30)]
    [InlineData("1430", 14, 30)]
    [InlineData("9h", 9, 0)]
    [InlineData("9h30", 9, 30)]
    [InlineData("7", 7, 0)]
    public void Reads_typed_times(string text, int hours, int minutes)
    {
        Assert.True(WalletText.TryParseTime(text, out var time));
        Assert.Equal(new TimeSpan(hours, minutes, 0), time);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("abc")]
    [InlineData("12:75")]
    public void Rejects_typed_times(string text) => Assert.False(WalletText.TryParseTime(text, out _));

    [Fact]
    public void Page_adds_with_a_time_and_a_new_category()
    {
        var vm = ViewModel();
        vm.OpenAddCommand.Execute(null);
        Assert.Equal("09:00", vm.NewTimeText); // now
        vm.NewAmount = "120k";
        vm.NewDescription = "vet";
        vm.NewDate = new DateTime(2026, 10, 2);
        vm.NewTimeText = "18h45";
        Assert.Equal(new TimeSpan(18, 45, 0), vm.NewTime);
        vm.AddCategoryName = "Pets";
        vm.AddCommand.Execute(null);
        Assert.False(vm.IsAddOpen);
        var t = _store.Transactions().Single().Value;
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 18, 45, 0, TimeSpan.FromHours(7)), t.OccurredAt);
        var category = _store.Category(t.CategoryId!)!;
        Assert.Equal(("Pets", CategoryKind.Expense), (category.Name, category.Kind));

        // The same name again is refused, and nothing is saved.
        vm.OpenAddCommand.Execute(null);
        vm.NewAmount = "50k";
        vm.AddCategoryName = "pets";
        vm.AddCommand.Execute(null);
        Assert.True(vm.IsAddOpen);
        Assert.Contains("already a category", vm.Message);
        Assert.Single(_store.Transactions());

        // A bad time is refused too.
        vm.AddCategoryName = "";
        vm.NewTimeText = "25:99";
        vm.AddCommand.Execute(null);
        Assert.True(vm.IsAddOpen);
    }

    [Fact]
    public void Page_edits_the_time()
    {
        var id = _store.AddManual(-100_000, "lunch", T0, WalletCategories.Food);
        var vm = ViewModel();
        var row = vm.Days[0].Rows[0];
        row.BeginEditCommand.Execute(null);
        Assert.Equal("09:00", row.EditTimeText);
        row.EditTimeText = "12:15";
        row.SaveEditCommand.Execute(null);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 12, 15, 0, TimeSpan.FromHours(7)), _store.Get(id)!.OccurredAt);
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

public sealed class WalletChartTests
{
    private static readonly TimeZoneInfo Ict = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 9, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Every_icon_exists_in_wpf_ui_below_ffff()
    {
        // Android's FluentIcons has the same names (checked when the list was made); WPF-UI draws a symbol as one
        // 16-bit char, so a value above U+FFFF would show a stray letter.
        foreach (var name in WalletIcons.Choices.Append(WalletIcons.Uncategorized).Concat(WalletCategories.BuiltIn.Select(c => c.Icon)))
        {
            Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(name + "24", out var symbol), name);
            Assert.True((int)symbol <= 0xFFFF, name);
        }
        Assert.All(WalletCategories.BuiltIn, c => Assert.InRange(c.Color, 0, WalletPalette.Slots));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.7, 1)]
    [InlineData(1.2, 2)]
    [InlineData(2.2, 2.5)]
    [InlineData(3, 5)]
    [InlineData(6, 10)]
    [InlineData(1_250_000, 2_000_000)]
    [InlineData(2_000_000, 2_000_000)]
    public void Nice_max(double value, double expected) => Assert.Equal(expected, WalletCharts.NiceMax(value), 6);

    [Fact]
    public void Layout_caps_columns_and_keeps_a_gap()
    {
        var model = new BarChartModel([new("1", 100, "a"), new("", 50, "b"), new("", 0, "c"), new("4", 25, "d", IsEmpty: true)], 80, "ref", 0);
        var g = WalletCharts.Layout(model, 400, 200, 20, 20);
        Assert.Equal(2, g.Bars.Count); // a zero and an empty slot draw nothing
        Assert.All(g.Bars, b => Assert.True(b.Width <= WalletCharts.MaxBarWidth));
        Assert.Equal(180, g.BaselineY);
        Assert.Equal(20, g.Bars[0].Y, 6); // 100 is the top of a 0–100 scale
        Assert.Equal(100, g.Bars[1].Y, 6);
        Assert.Equal(180 - 160 * 0.8, g.ReferenceY!.Value, 6);
        Assert.Equal(1, g.IndexAt(150));
        Assert.Equal(-1, g.IndexAt(401));
        Assert.Equal(["1", "4"], g.Labels.Select(l => l.Text));
        // Narrow: many slots, columns stay at least 1 px apart.
        var many = new BarChartModel(Enumerable.Range(1, 31).Select(i => new ChartBar("", i, "")).ToList(), null, "", 0);
        var narrow = WalletCharts.Layout(many, 300, 100, 0, 0);
        Assert.All(narrow.Bars.Zip(narrow.Bars.Skip(1)), p => Assert.True(p.Second.X - (p.First.X + p.First.Width) >= 1.9));
    }

    private static WalletStore Store() => new(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), new ManualTime(T0));

    [Fact]
    public void Daily_chart_with_days_to_come_and_the_budget_a_day()
    {
        var store = Store();
        store.AddManual(-100_000, "lunch", T0, WalletCategories.Food);
        store.AddManual(-50_000, "grab", T0.AddDays(-1), null);
        store.AddManual(-9_000_000, "savings", T0, WalletCategories.Transfer);
        var chart = WalletCharts.Daily(store.Transactions(), store.Categories(true), new DateOnly(2026, 10, 1), T0, Ict, 3_100_000);
        Assert.Equal(31, chart.Bars.Count);
        Assert.Equal(100_000, chart.Bars[3].Value); // transfers left out
        Assert.Equal(50_000, chart.Bars[2].Value);
        Assert.True(chart.Bars[4].IsEmpty);
        Assert.Equal(3, chart.DefaultIndex); // today
        Assert.Equal(100_000, chart.Reference!.Value, 3);
        Assert.StartsWith("Today · ", chart.Bars[3].Caption);
    }

    [Fact]
    public void Months_chart_opens_a_month()
    {
        var store = Store();
        store.AddManual(-100_000, "lunch", T0, WalletCategories.Food);
        store.AddManual(-300_000, "shoes", T0.AddMonths(-1), WalletCategories.Shopping);
        DateOnly? opened = null;
        var chart = WalletCharts.Months(store.Transactions(), store.Categories(true), new DateOnly(2026, 10, 1), T0, Ict, 6, m => opened = m);
        Assert.Equal(6, chart.Bars.Count);
        Assert.True(chart.Bars[5].IsEmphasis);
        Assert.False(chart.Bars[4].IsEmphasis);
        Assert.Equal(0, chart.Bars[4].Value); // money in
        Assert.Equal(300_000, chart.Bars[4].Value2); // spent
        Assert.True(chart.IsGrouped);
        Assert.Equal((CashFlowColors.MoneyIn, CashFlowColors.Spent, "Money in", "Spent"), (chart.Color, chart.Color2, chart.Name, chart.Name2));
        Assert.Null(chart.Bars[5].Command); // the month shown
        chart.Bars[4].Command!.Execute(null);
        Assert.Equal(new DateOnly(2026, 9, 1), opened);
    }

    [Fact]
    public void Grouped_columns_sit_side_by_side_with_a_gap()
    {
        var model = new BarChartModel([new("a", 100, "", Value2: 50), new("b", 0, "", Value2: 25)], null, "", 0);
        var g = WalletCharts.Layout(model, 200, 120, 20, 0);
        Assert.Equal(3, g.Bars.Count); // b has no money in
        var (first, second) = (g.Bars[0], g.Bars[1]);
        Assert.Equal((0, 1), (first.Series, second.Series));
        Assert.Equal(WalletCharts.BarGap, second.X - (first.X + first.Width), 6);
        Assert.Equal(first.Height / 2, second.Height, 6);
        Assert.True(first.X >= 0 && second.X + second.Width <= 100); // inside the first slot
    }

    [Fact]
    public void Cash_flow_bars_share_one_scale()
    {
        var store = Store();
        store.AddManual(10_000_000, "salary", T0, WalletCategories.Salary);
        store.AddManual(-12_500_000, "rent", T0, WalletCategories.Housing);
        var summary = WalletStats.Month(store.Transactions(), store.Categories(true), new DateOnly(2026, 10, 1), T0, Ict);
        var rows = WalletCharts.CashFlow(summary);
        Assert.Equal(["Money in", "Spent", "Net (spent more)"], rows.Select(r => r.Name));
        Assert.Equal([0.8, 1.0, 0.2], rows.Select(r => Math.Round(r.Fraction, 3)));
        Assert.StartsWith("−", rows[2].AmountText);
        Assert.Equal([CashFlowColors.MoneyIn, CashFlowColors.Spent, CashFlowColors.Net], rows.Select(r => r.Color));
    }

    [Fact]
    public void Donut_keeps_six_slices_and_folds_the_rest()
    {
        var store = Store();
        var ids = new[] { WalletCategories.Food, WalletCategories.Groceries, WalletCategories.Transport, WalletCategories.Shopping,
            WalletCategories.Bills, WalletCategories.Housing, WalletCategories.Health };
        for (var i = 0; i < ids.Length; i++) store.AddManual(-(i + 1) * 100_000, "x", T0, ids[i]);
        var summary = WalletStats.Month(store.Transactions(), store.Categories(true), new DateOnly(2026, 10, 1), T0, Ict);
        var donut = WalletCharts.Spending(summary, store.Categories(true));
        Assert.Equal(DonutModel.MaxSegments, donut.Segments.Count);
        Assert.Equal("Health", donut.Segments[0].Name);
        Assert.Equal(8, donut.Segments[0].Color);
        Assert.Equal("Other (2)", donut.Segments[^1].Name);
        Assert.Equal(-1, donut.Segments[^1].Color);
        Assert.Equal(300_000m, donut.Segments[^1].Amount); // 100k + 200k
        Assert.Equal(1, donut.Segments.Sum(s => s.Share), 6);
    }

    [Fact]
    public void Category_icon_and_colour()
    {
        var store = Store();
        Assert.True(store.SetCategoryLook(WalletCategories.Food, "DrinkCoffee", 7));
        var food = store.Category(WalletCategories.Food)!;
        Assert.Equal(("DrinkCoffee", 7, "Food & drinks"), (food.Icon, food.Color, food.Name));
        Assert.True(store.RenameCategory(WalletCategories.Food, "Cafe"));
        Assert.Equal("DrinkCoffee", store.Category(WalletCategories.Food)!.Icon); // kept by a rename
        store.SetCategoryLook(WalletCategories.Food, "NoSuchIcon", 99);
        Assert.Equal((WalletIcons.Fallback, 7), (store.Category(WalletCategories.Food)!.Icon, store.Category(WalletCategories.Food)!.Color));

        // A new category gets the colour its kind uses least.
        var pets = store.AddCategory("Pets", CategoryKind.Expense, "AnimalDog");
        var p = store.Category(pets)!;
        Assert.Equal("AnimalDog", p.Icon);
        Assert.InRange(p.Color, 1, WalletPalette.Slots);
        Assert.Equal(6, WalletPalette.NextSlot([1, 2, 3, 4, 5, 7, 8, 1, 2]));
    }

    [Fact]
    public void Page_makes_a_new_category_from_a_transaction()
    {
        var store = Store();
        var dir = new TempDir();
        using var settings = new SettingsStoreFactory(new HelmPaths(dir.Path));
        store.AddManual(-200_000, "vet", T0, null);
        var vm = new WalletViewModel(store, new WalletCapture(store, settings), settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), null,
            NullLogger<WalletViewModel>.Instance, Ict);
        var row = vm.ToCategorize.Single();
        row.NewCategoryName = "Pets";
        row.CreateCategoryCommand.Execute(null);
        Assert.Empty(vm.ToCategorize);
        var category = store.Category(store.Get(row.Id)!.CategoryId!)!;
        Assert.Equal(("Pets", CategoryKind.Expense), (category.Name, category.Kind));
        Assert.Equal("Pets", vm.SpendingDonut.Segments.Single().Name);
        settings.Dispose();
        dir.Dispose();
    }
}

internal static class WalletTestExtensions
{
    public static WalletTransaction ToTransaction(this ParsedTransaction p) =>
        new() { Amount = p.Amount, Description = p.Description, OccurredAt = p.OccurredAt, Bank = p.Bank };
}
