using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Settings;
using Helm.Modules.Wallet;
using Helm.Modules.Wallet.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Gallery;

/// <summary>Which Wallet a Gallery entry shows.</summary>
internal enum WalletStage
{
    /// <summary>A busy November: money in and out, five transactions waiting for a category, funds, bills.</summary>
    Money,
    /// <summary>The same, scrolled to the month's transactions.</summary>
    Transactions,
    /// <summary>The sheet with every transaction waiting for a category.</summary>
    ToCategorize,
    /// <summary>The add form, opened from the quick add's options with what was typed.</summary>
    Add,
    /// <summary>A bank transaction's details (its category one tap away, the bank's notification).</summary>
    Details,
    /// <summary>A transaction's money into the debt book.</summary>
    DebtForm,
    /// <summary>A fund that went over.</summary>
    Fund,
    /// <summary>A new fund.</summary>
    FundForm,
    /// <summary>The Debts tab.</summary>
    Debts,
    /// <summary>A person in the debt book.</summary>
    Person,
    /// <summary>A new debt.</summary>
    AddDebt,
    /// <summary>The Goals tab.</summary>
    Goals,
    /// <summary>A savings goal, setting money aside.</summary>
    Goal,
    /// <summary>The Insights tab.</summary>
    Insights,
    /// <summary>The settings sheet (the gear), on a PC.</summary>
    Settings,
    /// <summary>The phone before notification access is on, and its settings.</summary>
    PhoneSetup,
    /// <summary>The settings sheet on the phone (bank notifications, the widget).</summary>
    PhoneSettings,
    /// <summary>No transaction yet.</summary>
    FirstRun,
    /// <summary>No savings goal yet.</summary>
    GoalsFirstRun,
}

/// <summary>The real Wallet view over in-memory stores: six months of a Hà Nội office worker's money, in VND.</summary>
internal static class WalletDemo
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Local;

    public static Control Create(FakeModule module, WalletStage stage)
    {
        var transactions = new MemorySynced<WalletTransaction>();
        var store = new WalletStore(transactions, new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>());
        var debtBook = new DebtBook(new MemorySynced<WalletDebt>(), store);
        var fundBook = new FundBook(new MemorySynced<WalletFund>(), store);
        var billBook = new BillBook(new MemorySynced<WalletBill>(), store);
        var goalBook = new GoalBook(new MemorySynced<WalletGoal>(), store);
        var seed = stage is not (WalletStage.FirstRun);
        if (seed) Seed(store, transactions, fundBook, billBook, debtBook, goalBook, goals: stage != WalletStage.GoalsFirstRun);

        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var settings = new SettingsStoreFactory(new HelmPaths(dir));
        var phone = stage is WalletStage.PhoneSetup or WalletStage.PhoneSettings;
        if (phone)
        {
            settings.Get<WalletSettings>(WalletIds.ModuleId).Update(s =>
            {
                s.ListenEnabled = true;
                s.Unread.Add(new UnreadNotification
                {
                    At = DateTimeOffset.Now.AddHours(-3),
                    Bank = "Techcombank",
                    Title = "Techcombank",
                    Text = "Quy khach da thanh toan thanh cong 1.250.000 VND cho don hang Shopee 2611XYZ. Cam on quy khach.",
                });
            });
        }
        var ui = new AvaloniaUi();
        var dialogs = new YesDialogs();
        var services = new Services
        {
            [typeof(DebtsViewModel)] = new DebtsViewModel(debtBook, ui, dialogs, NullLogger<DebtsViewModel>.Instance),
            [typeof(FundBook)] = fundBook,
            [typeof(BillBook)] = billBook,
            [typeof(GoalBook)] = goalBook,
        };
        if (phone) services[typeof(IWalletPlatform)] = new PhonePlatform();
        var vm = new WalletViewModel(store, new WalletCapture(store, settings), settings, ui, dialogs, new NoClipboard(), services,
            NullLogger<WalletViewModel>.Instance);
        var view = new WalletView(module, vm, new Host(phone));

        void Later(Action action) => view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
        switch (stage)
        {
            case WalletStage.Transactions:
                Later(() =>
                {
                    if (view.FindControl<ScrollViewer>("Scroller") is { } scroller)
                        scroller.Offset = new Avalonia.Vector(0, scroller.Bounds.Width < 720 ? 1750 : 640);
                });
                break;
            case WalletStage.ToCategorize:
                vm.OpenToCategorizeCommand.Execute(null);
                break;
            case WalletStage.Add:
                vm.QuickText = "65k bánh mì Huỳnh Hoa";
                vm.OpenQuickOptionsCommand.Execute(null);
                break;
            case WalletStage.Details:
                vm.Days.SelectMany(d => d.Rows).First(r => r.HasOriginal && r.IsCategorized && !r.IsDebt).OpenCommand.Execute(null);
                break;
            case WalletStage.DebtForm:
                vm.ToCategorize.First(r => r.Title.Contains("NGUYEN VAN NAM", StringComparison.Ordinal)).OpenDebtFormCommand.Execute(null);
                break;
            case WalletStage.Fund:
                vm.Funds!.Rows.First(r => r.IsOver).OpenCommand.Execute(null);
                break;
            case WalletStage.FundForm:
                vm.Funds!.OpenAddCommand.Execute(null);
                vm.Funds.FormName = "Cà phê & đi chơi";
                vm.Funds.FormAmount = "800.000";
                break;
            case WalletStage.Debts:
                view.ShowTab(WalletTab.Debts);
                break;
            case WalletStage.Person:
                view.ShowTab(WalletTab.Debts);
                vm.Debts!.People.First().OpenCommand.Execute(null);
                break;
            case WalletStage.AddDebt:
                view.ShowTab(WalletTab.Debts);
                vm.Debts!.OpenAddCommand.Execute(null);
                break;
            case WalletStage.Goals or WalletStage.GoalsFirstRun:
                view.ShowTab(WalletTab.Goals);
                break;
            case WalletStage.Goal:
                view.ShowTab(WalletTab.Goals);
                vm.Goals!.Rows.First().OpenCommand.Execute(null);
                vm.Goals.PutAmount = "500.000";
                break;
            case WalletStage.Insights:
                view.ShowTab(WalletTab.Insights);
                break;
            case WalletStage.Settings or WalletStage.PhoneSettings:
                Later(() => view.OpenSettings());
                break;
        }
        return view;
    }

    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new(day.ToDateTime(new TimeOnly(hour, minute)), Zone.GetUtcOffset(day.ToDateTime(new TimeOnly(hour, minute))));

    private static void Seed(WalletStore store, MemorySynced<WalletTransaction> transactions, FundBook funds, BillBook bills, DebtBook debts, GoalBook goalBook,
        bool goals = true)
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var month = new DateOnly(today.Year, today.Month, 1);
        var random = new Random(11);
        var coffee = store.AddCategory("Cà phê", CategoryKind.Expense, "DrinkCoffee", 9);
        decimal balance = 3_150_000;

        void Bank(DateOnly day, int hour, int minute, decimal amount, string description, string? category, string bank = "Techcombank", bool auto = false,
            string note = "")
        {
            var at = At(day, hour, minute);
            if (at > now) return;
            balance += amount;
            var account = bank == "ACB" ? "2458 6677" : "1903 6688 991234";
            var sign = amount < 0 ? "-" : "+";
            transactions.Add(new WalletTransaction
            {
                Amount = amount,
                Bank = bank,
                Account = account.Replace(" ", "", StringComparison.Ordinal),
                Balance = balance,
                Description = description,
                OccurredAt = at,
                CategoryId = category,
                AutoCategorized = auto,
                Note = note,
                Source = TransactionSource.Notification,
                Original = bank == "ACB"
                    ? $"ACB: TK {account[..4]}xxxx({at:dd/MM/yy HH:mm}) {sign}{Math.Abs(amount):#,0}VND. GD: {description}. So du: {balance:#,0}VND"
                    : $"TK {account[..4]}xxxx{account[^4..]} So tien GD:{sign}{Math.Abs(amount):#,0} So du:{balance:#,0} {description}",
                CreatedAt = at,
            });
        }

        void Cash(DateOnly day, int hour, int minute, decimal amount, string description, string? category, string note = "")
        {
            var at = At(day, hour, minute);
            if (at > now) return;
            transactions.Add(new WalletTransaction
            {
                Amount = amount,
                Description = description,
                OccurredAt = at,
                CategoryId = category,
                Note = note,
                Source = TransactionSource.Manual,
                CreatedAt = at,
            });
        }

        // Five months before this one: the six-month chart and the comparison have something to show.
        for (var m = 5; m >= 1; m--)
        {
            var first = month.AddMonths(-m);
            var days = DateTime.DaysInMonth(first.Year, first.Month);
            Bank(first.AddDays(4), 9, 12, 21_500_000, "CTY TNHH AN PHAT TRA LUONG", WalletCategories.Salary, "ACB");
            Bank(first, 8, 5, -5_000_000, "CK tien nha thang " + first.Month, WalletCategories.Housing);
            for (var d = 0; d < days; d++)
            {
                var day = first.AddDays(d);
                Bank(day, 7, 40 + random.Next(15), -random.Next(30, 60) * 1000, "THANH TOAN QR PHO THIN", WalletCategories.Food, auto: true);
                if (random.NextDouble() < 0.6) Bank(day, 12, 10, -random.Next(40, 90) * 1000, "GRABFOOD", WalletCategories.Food);
                if (random.NextDouble() < 0.5) Bank(day, 18, 30, -random.Next(25, 80) * 1000, "GRAB*TRIP", WalletCategories.Transport);
                if (random.NextDouble() < 0.12) Bank(day, 20, 15, -random.Next(150, 900) * 1000, "SHOPEEPAY*SHOPEE", WalletCategories.Shopping);
                if (random.NextDouble() < 0.15) Bank(day, 19, 5, -random.Next(180, 450) * 1000, "BACH HOA XANH", WalletCategories.Groceries);
            }
            Bank(first.AddDays(9), 10, 0, -random.Next(900, 1400) * 1000, "EVN HANOI TIEN DIEN", WalletCategories.Bills);
        }

        // This month, day by day.
        Bank(month, 8, 5, -5_000_000, "CK tien nha thang " + month.Month, WalletCategories.Housing, note: "Tiền nhà tháng " + month.Month);
        Bank(month.AddDays(4), 9, 12, 21_500_000, "CTY TNHH AN PHAT TRA LUONG", WalletCategories.Salary, "ACB");
        Bank(month.AddDays(14), 15, 40, 2_000_000, "THUONG DU AN QUY 4", WalletCategories.Bonus, "ACB");
        for (var d = 0; d < today.Day; d++)
        {
            var day = month.AddDays(d);
            Bank(day, 7, 45, -random.Next(35, 55) * 1000, "THANH TOAN QR PHO THIN", WalletCategories.Food, auto: true);
            if (d % 2 == 0) Cash(day, 9, 30, -random.Next(25, 45) * 1000, "Cà phê Cộng", coffee);
            if (random.NextDouble() < 0.55) Bank(day, 12, 15, -random.Next(45, 95) * 1000, "GRABFOOD", WalletCategories.Food);
            if (random.NextDouble() < 0.45) Bank(day, 18, 40, -random.Next(28, 75) * 1000, "GRAB*TRIP", WalletCategories.Transport);
            if (random.NextDouble() < 0.12) Bank(day, 19, 20, -random.Next(180, 420) * 1000, "BACH HOA XANH", WalletCategories.Groceries);
        }
        Bank(month.AddDays(6), 21, 10, -489_000, "SHOPEEPAY*SHOPEE", WalletCategories.Shopping);
        Bank(month.AddDays(11), 20, 5, -790_000, "UNIQLO VINCOM BA TRIEU", WalletCategories.Shopping);
        Bank(month.AddDays(17), 22, 30, -360_000, "CGV VINCOM", WalletCategories.Entertainment);
        Cash(today, 8, 10, -30_000, "Gửi xe", WalletCategories.Transport);

        // Waiting for a category: what the bank said, nothing Helm knows yet.
        Bank(today, 11, 42, -186_000, "QR - CONG TY CP DICH VU DI DONG TRUC TUYEN", null);
        Bank(today, 10, 5, -1_250_000, "CK den NGUYEN VAN NAM", null);
        Bank(today.AddDays(-1), 21, 18, -420_000, "THE DIEN THOAI VIETTEL", null);
        Bank(today.AddDays(-1), 13, 2, -95_000, "PETSHOP MEOMEO", null);
        Bank(today.AddDays(-2), 17, 30, 350_000, "TRAN THI HA chuyen tien", null, "ACB");

        // Funds: eating is low, shopping went over.
        funds.Add("Ăn uống", 5_500_000, true, month, [WalletCategories.Food, WalletCategories.Groceries, coffee], alertPercent: 80);
        funds.Add("Đi lại", 1_200_000, true, month, [WalletCategories.Transport]);
        funds.Add("Nhà & hoá đơn", 7_000_000, true, month, [WalletCategories.Housing, WalletCategories.Bills]);
        funds.Add("Mua sắm & vui chơi", 1_400_000, true, month, [WalletCategories.Shopping, WalletCategories.Entertainment], carryOver: true);
        store.SetMonthlyBudget(15_000_000);

        // Bills: rent and internet paid, electricity paid (it varies), English late, Netflix coming.
        var internet = bills.Add("Internet Viettel", 220_000, 5, WalletCategories.Bills);
        bills.Pay(internet, null, At(month.AddDays(4), 20, 0));
        var power = bills.Add("Tiền điện", null, 10, WalletCategories.Bills);
        bills.Pay(power, 1_184_000, At(month.AddDays(9), 19, 30));
        // Due three days ago and not paid: late (it started two months ago).
        var english = bills.Add("Học phí tiếng Anh", 1_800_000, Math.Max(1, today.Day - 3), WalletCategories.Education);
        bills.Restore(english, bills.Get(english)! with { StartMonth = month.AddMonths(-2), PaidMonths = [month.AddMonths(-2), month.AddMonths(-1)] });
        bills.Add("Netflix", 260_000, Math.Min(28, today.Day + 2), WalletCategories.Entertainment);

        // The debt book.
        debts.Add("Nam", 500_000, DebtEntryKind.OwesMe, "Tiền vé xem bóng đá", At(today.AddDays(3), 18, 0));
        debts.Add("Linh", 1_200_000, DebtEntryKind.IOwe, "Mượn mua quà sinh nhật mẹ", At(today.AddDays(-2), 18, 0));
        debts.Add("Tuấn", 2_000_000, DebtEntryKind.OwesMe, "Cho mượn sửa xe", At(today.AddDays(12), 18, 0));
        debts.Add("Tuấn", 500_000, DebtEntryKind.Repayment, "Trả trước một phần");
        debts.Add("Hà", 300_000, DebtEntryKind.OwesMe, "Ăn lẩu");
        debts.Add("Hà", 300_000, DebtEntryKind.Repayment);

        if (!goals) return;
        var pc = goalBook.Add("Mini PC Beelink", 12_990_000, today.AddMonths(3), 5_000_000, Zone);
        goalBook.Put(pc, 1_500_000, Zone);
        goalBook.Put(pc, 1_300_000, Zone);
        var trip = goalBook.Add("Vé máy bay Đà Lạt", 3_200_000, today.AddDays(20), 2_000_000, Zone);
        goalBook.Put(trip, 1_200_000, Zone);
        goalBook.Add("Tai nghe Sony WH-1000XM5", 6_490_000, null, 1_200_000, Zone);
        var keyboard = goalBook.Add("Bàn phím cơ", 1_890_000, null, 1_890_000, Zone);
        goalBook.Buy(keyboard, 1_850_000, WalletCategories.Shopping, Zone);
    }

    /// <summary>The services the view model looks up (its optional parts).</summary>
    private sealed class Services : Dictionary<Type, object>, IServiceProvider
    {
        public object? GetService(Type serviceType) => TryGetValue(serviceType, out var service) ? service : null;
    }

    /// <summary>A phone that has not been given notification access yet.</summary>
    private sealed class PhonePlatform : IWalletPlatform
    {
        public bool HasNotificationAccess => false;

        public void OpenNotificationAccess() { }

        public bool MayRestrictNotificationAccess => true;

        public void OpenAppDetails() { }

        public bool CanPinWidget => true;

        public void PinWidget() { }
    }

    private sealed class Host(bool phone) : IWalletPageHost
    {
        public void Opened() { }

        public void ExportCsv(string csv) { }

        public string ExportText => phone ? "Share CSV" : "Export…";

        public string QuickCaptureHint => phone
            ? "In Quick Capture (Share → Save to Helm, or the Quick Settings tile), type /s 50k coffee for money out or /s +2tr bonus for money in."
            : "In Quick Capture (Win+Alt+N), type /s 50k coffee for money out or /s +2tr bonus for money in.";
    }
}
