using System.Windows;
using Helm.Core.Settings;
using Helm.Modules.Wallet;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The Wallet page and its pop-ups load with real data, built off-screen: no window is shown. With
/// HELM_WALLET_SNAPSHOTS set to a folder each one is rendered to a PNG there, for checking the layout by eye.
/// </summary>
public sealed class WalletViewsTests : IDisposable
{
    private static readonly TimeZoneInfo Ict = TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 20, 30, 0, TimeSpan.FromHours(7));
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void The_page_and_every_popup_load() => WpfHost.Run(() =>
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        using var settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
        var time = new ManualTime(T0);
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), time);
        var book = new DebtBook(new MemorySynced<WalletDebt>(), wallet, time);
        var funds = new FundBook(new MemorySynced<WalletFund>(), wallet);
        Seed(wallet, book, funds);
        var debts = new DebtsViewModel(book, new InlineUi(), new AcceptDialogs(), NullLogger<DebtsViewModel>.Instance);
        var vm = new WalletViewModel(wallet, new WalletCapture(wallet, settings), settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), null,
            NullLogger<WalletViewModel>.Instance, Ict, debts, funds);
        var page = new WalletContentPage(new WalletModule { IsEnabled = true }, vm);
        var overlay = new WalletOverlay(vm);
        var size = new Size(1280, 1500);
        var popupSize = new Size(1280, 960);

        Assert.Equal(6, vm.Funds!.Rows.Count);
        Assert.Equal(3, vm.ToCategorize.Count);
        Layout(page, size, "1-money");

        vm.OpenAddCommand.Execute(null);
        vm.NewAmount = "35k";
        vm.NewDescription = "Bánh mì";
        vm.NewPicks.First(p => p.Id == WalletCategories.Food).PickCommand.Execute(null);
        Show(overlay, popupSize, "2-add");
        vm.ClosePopupCommand.Execute(null);

        vm.OpenToCategorizeCommand.Execute(null);
        Show(overlay, popupSize, "3-to-categorize");
        vm.ToCategorize[0].OpenCommand.Execute(null);
        Show(overlay, popupSize, "4-details");
        vm.Selected!.BeginEditCommand.Execute(null);
        Show(overlay, popupSize, "5-edit");
        vm.DismissPopupCommand.Execute(null);
        vm.Selected!.OpenDebtFormCommand.Execute(null);
        Show(overlay, popupSize, "6-debt");
        vm.ClosePopupCommand.Execute(null);

        var home = vm.Funds.Rows.First(r => r.IsOver);
        home.OpenCommand.Execute(null);
        Show(overlay, popupSize, "7-fund");
        vm.Funds.OpenCoverCommand.Execute(null);
        Show(overlay, popupSize, "8-cover");
        vm.DismissPopupCommand.Execute(null);
        vm.Funds.EditSelectedCommand.Execute(null);
        Show(overlay, popupSize, "9-fund-form");
        vm.ClosePopupCommand.Execute(null);

        vm.ShowDebtsCommand.Execute(null);
        Layout(page, new Size(1280, 900), "10-debts");
        debts.OpenAddCommand.Execute(null);
        Show(overlay, popupSize, "11-add-debt");
        vm.ClosePopupCommand.Execute(null);
        debts.People[0].OpenCommand.Execute(null);
        Show(overlay, popupSize, "12-person");
        vm.ClosePopupCommand.Execute(null);
    });

    [Fact]
    public void The_goals_page_and_its_popups_load() => WpfHost.Run(() =>
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        using var settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
        var time = new ManualTime(T0);
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), time);
        wallet.AddManual(20_000_000, "Lương", T0.AddDays(-20), WalletCategories.Salary);
        wallet.AddManual(-1_580_000, "Đi chợ", T0.AddDays(-2), WalletCategories.Groceries);
        var goals = new GoalBook(new MemorySynced<WalletGoal>(), wallet);
        var pc = goals.Add("Mini PC", 12_990_000, new DateOnly(2027, 1, 31), 2_000_000, Ict);
        goals.Put(pc, 2_350_000, Ict);
        var keyboard = goals.Add("Bàn phím cơ", 1_850_000, null, 1_850_000, Ict);
        goals.Add("Tai nghe", 2_400_000, null, 300_000, Ict);
        var trip = goals.Add("Đà Lạt", 3_000_000, null, 500_000, Ict);
        goals.GiveUp(trip);
        var vm = new WalletViewModel(wallet, new WalletCapture(wallet, settings), settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), null,
            NullLogger<WalletViewModel>.Instance, Ict, goals: goals);
        var page = new WalletContentPage(new WalletModule { IsEnabled = true }, vm);
        var overlay = new WalletOverlay(vm);
        var popupSize = new Size(1280, 960);

        vm.ShowGoalsCommand.Execute(null);
        var photo = Photo();
        vm.Goals!.Rows.First(r => r.Id == pc).Photo = photo;
        vm.Goals.Rows.First(r => r.Id == keyboard).Photo = photo;
        Assert.Equal(3, vm.Goals.Rows.Count);
        Layout(page, new Size(1280, 900), "20-goals");

        vm.ShowGoal(pc);
        vm.Goals.PutAmount = "200k";
        Show(overlay, popupSize, "21-goal");
        vm.Goals.OpenTakeOutCommand.Execute(null);
        Show(overlay, popupSize, "22-take-out");
        vm.ClosePopupCommand.Execute(null);
        vm.ShowGoal(keyboard);
        Show(overlay, popupSize, "23-goal-ready");
        vm.Goals.OpenBoughtCommand.Execute(null);
        Show(overlay, popupSize, "24-bought");
        vm.ClosePopupCommand.Execute(null);
        vm.Goals.OpenAddCommand.Execute(null);
        vm.Goals.FormName = "Màn hình";
        vm.Goals.FormPrice = "4.500.000";
        vm.Goals.FormDue = new DateTime(2027, 3, 1);
        vm.Goals.FormPhoto = photo;
        Show(overlay, popupSize, "25-goal-form");
        vm.ClosePopupCommand.Execute(null);
    });

    /// <summary>A stand-in product photo: a box on a warm background, as a JPEG.</summary>
    private static byte[] Photo()
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x7A), System.Windows.Media.Color.FromRgb(0xE8, 0x6A, 0x9A), 45),
                null, new Rect(0, 0, 400, 300));
            dc.DrawRoundedRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x46, 0x58)), null, new Rect(110, 90, 180, 130), 18, 18);
            dc.DrawEllipse(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x39, 0xE6, 0xFF)), null, new Point(140, 190), 8, 8);
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x7A, 0x3D)), null, new Rect(110, 205, 180, 8));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(400, 300, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static void Seed(WalletStore wallet, DebtBook book, FundBook funds)
    {
        var october = new DateOnly(2026, 10, 1);
        void Spend(decimal amount, string description, string? category, int day, int hour = 12) =>
            wallet.AddManual(-amount, description, new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.FromHours(7)), category);
        Spend(45_000, "GRAB*A-5XK2", WalletCategories.Transport, 24, 20);
        Spend(59_000, "Highlands Coffee", WalletCategories.Food, 24, 12);
        Spend(500_000, "CK den 0987xxx123 NGUYEN VAN NAM", null, 24, 9);
        Spend(32_000, "QR VNPAY 7-ELEVEN Q1", null, 23, 21);
        Spend(214_000, "Bach Hoa Xanh", WalletCategories.Groceries, 23, 18);
        Spend(389_000, "ShopeePay", WalletCategories.Shopping, 23, 8);
        Spend(200_000, "MOMO TOPUP", null, 22, 19);
        Spend(612_000, "EVN tien dien T10", WalletCategories.Bills, 21);
        Spend(180_000, "CGV Vincom", WalletCategories.Entertainment, 20);
        Spend(126_000, "Pharmacity", WalletCategories.Health, 20);
        Spend(1_250_000, "Shopee", WalletCategories.Shopping, 15);
        Spend(1_000_000, "Birthday gift for Mum", WalletCategories.Family, 14);
        Spend(4_500_000, "Rent November", WalletCategories.Housing, 5);
        Spend(230_000, "VIETTEL cuoc T10", WalletCategories.Bills, 3);
        wallet.AddManual(18_000_000, "TRA LUONG THANG 10", new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(7)), WalletCategories.Salary);
        funds.Add("Eating", 900_000, true, october, [WalletCategories.Food, WalletCategories.Groceries]);
        funds.Add("Getting around", 400_000, true, october, [WalletCategories.Transport]);
        funds.Add("Home & bills", 5_000_000, true, october, [WalletCategories.Housing, WalletCategories.Bills], alertPercent: 90);
        funds.Add("Fun & shopping", 1_850_000, true, october, [WalletCategories.Entertainment, WalletCategories.Shopping], carryOver: true);
        funds.Add("Health", 500_000, true, october, [WalletCategories.Health], carryOver: true);
        funds.Add("Da Lat trip", 2_000_000, false, october, [WalletCategories.Travel]);
        book.Add("Nam", 1_500_000, DebtEntryKind.OwesMe, "Trip to Da Lat");
        book.Add("Linh", 500_000, DebtEntryKind.IOwe, "Concert tickets");
    }

    /// <summary>The overlay at the size of the area it covers (on the page it sits on the adorner layer).</summary>
    private static void Show(WalletOverlay overlay, Size size, string name)
    {
        overlay.Place();
        Layout(overlay, size, name);
    }

    private static void Layout(FrameworkElement target, Size size, string name)
    {
        target.Measure(size);
        target.Arrange(new Rect(size));
        target.UpdateLayout();
        var idle = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => idle.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(idle);
        target.UpdateLayout();
        if (Environment.GetEnvironmentVariable("HELM_WALLET_SNAPSHOTS") is not { Length: > 0 } folder) return;
        var host = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("ApplicationBackgroundBrush"),
            Width = size.Width,
            Height = size.Height,
        };
        host.Measure(size);
        host.Arrange(new Rect(size));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Render(target);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, $"{name}.png"));
        encoder.Save(file);
    }
}
