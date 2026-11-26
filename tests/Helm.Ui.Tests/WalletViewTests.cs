using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Modules.Wallet;
using Helm.Modules.Wallet.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Wallet page (Helm.Modules.Wallet.UI) with its real view models over the Gallery's in-memory stores.</summary>
public sealed class WalletViewTests : IDisposable
{
    private const string MoneyHint = "Tap a category to file it. Tap the transaction for anything else.";
    private readonly FakeModule _module = new("wallet", "Wallet");

    public void Dispose()
    {
        while (Sheet.CloseTopmost()) { }
    }

    private static (Window Window, WalletView View, WalletViewModel Vm) Show(FakeModule module, WalletStage stage = WalletStage.Money, double width = 1200)
    {
        var view = (WalletView)WalletDemo.Create(module, stage);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        return (window, view, (WalletViewModel)view.DataContext!);
    }

    /// <summary>What is on screen, the page and the sheets over it.</summary>
    private static List<TextBlock> Shown(Window window, string text) =>
        window.GetVisualDescendants().Concat(OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? [])
            .OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).Distinct().ToList();

    private static void Run()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Each_tab_replaces_the_whole_page_under_the_tabs()
    {
        var (window, view, vm) = Show(_module);
        Assert.Single(Shown(window, MoneyHint));

        view.ShowTab(WalletTab.Debts);
        Run();
        Assert.True(vm.IsDebtsView);
        Assert.Single(Shown(window, "Outstanding"));
        Assert.Empty(Shown(window, MoneyHint));

        view.ShowTab(WalletTab.Goals);
        Run();
        Assert.True(vm.IsGoalsView);
        Assert.Single(Shown(window, "Balance you can spend"));

        view.ShowTab(WalletTab.Insights);
        Run();
        Assert.True(vm.IsMoneyView);
        Assert.Single(Shown(window, "Spending by category"));
        Assert.Empty(Shown(window, "Outstanding"));

        // Tapping a tab does the same.
        window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "MoneyTab").IsChecked = true;
        Run();
        Assert.Equal(WalletTab.Money, view.Tab);
        Assert.Single(Shown(window, MoneyHint));
        window.Close();
    }

    [AvaloniaFact]
    public void A_transaction_opened_from_the_list_to_categorize_goes_back_to_it()
    {
        var (window, view, vm) = Show(_module);
        vm.OpenToCategorizeCommand.Execute(null);
        Run();
        var sheet = view.Popup;
        Assert.NotNull(sheet);
        Assert.Contains(Shown(window, "To categorize"), t => t.FontSize >= 20);

        vm.ToCategorize[0].OpenCommand.Execute(null);
        Run();
        Assert.Equal(WalletPopup.Details, vm.Popup);
        Assert.Same(sheet, view.Popup);
        Assert.Single(Shown(window, "The bank's notification"));

        // Back (a tap outside, Escape) goes one step back, then closes the sheet.
        Assert.True(Sheet.CloseTopmost());
        Run();
        Assert.Equal(WalletPopup.ToCategorize, vm.Popup);
        Assert.Same(sheet, view.Popup);
        Assert.True(Sheet.CloseTopmost());
        Run();
        Assert.Equal(WalletPopup.None, vm.Popup);
        Assert.Null(view.Popup);
        window.Close();
    }

    [AvaloniaFact]
    public void The_sheet_shows_the_part_of_the_pop_up_and_nothing_else()
    {
        var (window, view, vm) = Show(_module, WalletStage.Fund);
        Assert.NotNull(view.Popup);
        var popup = OverlayLayer.GetOverlayLayer(window)!.GetVisualDescendants().OfType<WalletPopupView>().Single();
        Assert.True(popup.Bounds.Height > 200, $"The sheet is drawn ({popup.Bounds})");
        Assert.Single(Shown(window, "Spent from it this month"));
        Assert.Empty(Shown(window, "Add a transaction"));
        window.Close();
    }

    [AvaloniaFact]
    public void The_quick_add_saves_spending_in_one_line()
    {
        var (window, _, vm) = Show(_module);
        var before = vm.Days.Sum(d => d.Rows.Count);
        vm.QuickText = "45k bánh mì";
        Assert.True(vm.QuickReads);
        Assert.Contains("45,000", vm.QuickHint.Replace('.', ','));

        vm.QuickAddCommand.Execute(null);
        Run();
        Assert.Equal("", vm.QuickText);
        Assert.Contains(vm.Days.First().Rows, r => r.Title == "bánh mì" && r.AmountText.Contains("45"));
        Assert.True(vm.Days.Sum(d => d.Rows.Count) >= before);
        window.Close();
    }

    [AvaloniaFact]
    public void One_tap_on_a_suggested_category_files_a_transaction_from_the_page()
    {
        var (window, _, vm) = Show(_module);
        var waiting = vm.ToCategorize.Count;
        var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "ToCategorizeList");
        Assert.Equal(MoneyView.ToCategorizeOnPage, list.ItemCount);

        vm.ToCategorize[0].Suggestions[0].PickCommand.Execute(null);
        Run();
        Assert.Equal(waiting - 1, vm.ToCategorize.Count);
        Assert.Single(Shown(window, $"{waiting - 1}"));
        window.Close();
    }

    [AvaloniaFact]
    public void Deleting_asks_in_a_glass_sheet_over_the_details()
    {
        var (window, view, vm) = Show(_module, WalletStage.Details);
        Assert.NotNull(view.Popup);
        vm.Selected!.DeleteCommand.Execute(null);
        Run();
        Assert.Single(Shown(window, "Delete this transaction?"));
        window.Close();
    }

    [AvaloniaFact]
    public void The_gear_opens_the_settings_sheet_whose_switch_turns_the_tool_off()
    {
        var (window, view, _) = Show(_module);
        Assert.NotNull(view.OpenSettings());
        Run();
        Assert.Single(Shown(window, "Wallet is on"));
        // The PC reads nothing itself: it says where that happens.
        Assert.Single(Shown(window, "Bank notifications are read on your phone"));
        var toggle = OverlayLayer.GetOverlayLayer(window)!.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "EnableSwitch");
        toggle.IsChecked = false;
        Run();

        Assert.False(_module.IsEnabled);
        Assert.Single(Shown(window, "Wallet is turned off"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_without_notification_access_is_asked_for_it_first()
    {
        var (window, _, vm) = Show(_module, WalletStage.PhoneSetup, width: 390);
        Assert.True(vm.NeedsAccess);
        Assert.Single(Shown(window, "Turn on notification access"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_stacks_the_bills_and_funds_before_the_transactions()
    {
        var (window, view, _) = Show(_module, width: 390);
        Assert.Contains("narrow", view.Classes);
        var money = view.GetVisualDescendants().OfType<MoneyView>().Single();
        var stats = money.GetVisualDescendants().OfType<UniformGrid>().Single(g => g.Name == "StatGrid");
        Assert.Equal(2, stats.Columns);
        var main = money.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "MainColumn");
        var parts = main.Children.Select(c => c.Name).ToList();
        Assert.Equal(["AddPart", "ToCategorizePart", "SidePart", "TransactionsPart"], parts);
        window.Close();

        var (wide, wideView, _) = Show(new FakeModule("wallet", "Wallet"), width: 1300);
        var side = wideView.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "WideSide");
        Assert.Contains(side.Children, c => c.Name == "SidePart");
        wide.Close();
    }

    [AvaloniaFact]
    public void First_run_shows_the_way_in_instead_of_empty_numbers()
    {
        var (window, _, vm) = Show(_module, WalletStage.FirstRun);
        Assert.True(vm.IsFirstRun);
        Assert.Single(Shown(window, "Know where your money goes"));
        Assert.Empty(Shown(window, "Transactions"));
        window.Close();
    }
}
