using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Helm.Modules.Wallet.Ui;

/// <summary>
/// The Money tab of <see cref="WalletView"/>: the month and its numbers, the quick add, the transactions waiting for a
/// category (the first few, the rest in a sheet), the bills and the funds, and the month's transactions. On a wide screen
/// the bills and the funds move to a column beside the transactions.
/// </summary>
public partial class MoneyView : UserControl
{
    /// <summary>Transactions to categorize listed on the page; "See all" opens the rest in a sheet.</summary>
    public const int ToCategorizeOnPage = 3;

    /// <summary>From this width the bills and the funds sit beside the transactions.</summary>
    public const double TwoColumns = 900;

    private readonly WalletViewModel? _viewModel;
    private bool? _wide;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public MoneyView() => InitializeComponent();

    public MoneyView(WalletViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        AmountGrouping.Attach(this);
        AttachedToVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged += OnChanged;
            viewModel.ToCategorize.CollectionChanged += OnToCategorizeChanged;
            if (viewModel.Funds is { } funds) funds.PropertyChanged += OnChanged;
            ShowToCategorize();
            UpdateStats();
            SyncFilter();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged -= OnChanged;
            viewModel.ToCategorize.CollectionChanged -= OnToCategorizeChanged;
            if (viewModel.Funds is { } funds) funds.PropertyChanged -= OnChanged;
        };
        Arrange(wide: false);
    }

    /// <summary>The quick add, where the keyboard goes when the tab opens on a computer.</summary>
    public TextBox QuickAddBox => QuickBox;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Arrange(e.NewSize.Width >= TwoColumns);
        StatGrid.Columns = e.NewSize.Width < 620 ? 2 : FourthStatShown() ? 4 : 3;
        FirstRunSteps.Columns = e.NewSize.Width < 620 ? 1 : 3;
        QuickBox.PlaceholderText = e.NewSize.Width < 620 ? "Add spending: 45k cà phê" : "Add spending: 45k cà phê — press Enter";
    }

    /// <summary>
    /// One column (a phone, a narrow window): add → to categorize → bills and funds → transactions. Two columns: the
    /// bills and the funds beside them.
    /// </summary>
    private void Arrange(bool wide)
    {
        if (_wide == wide) return;
        _wide = wide;
        foreach (var part in new Control[] { AddPart, ToCategorizePart, SidePart, TransactionsPart })
            (part.Parent as Panel)?.Children.Remove(part);
        if (wide)
        {
            WideMain.Children.AddRange([AddPart, ToCategorizePart, TransactionsPart]);
            WideSide.Children.Add(SidePart);
            WideSide.Margin = new Thickness(0, 10, 0, 0);
            SidePart.Margin = default;
        }
        else
        {
            MainColumn.Children.AddRange([AddPart, ToCategorizePart, SidePart, TransactionsPart]);
            SidePart.Margin = new Thickness(0, 26, 0, 0);
        }
        MainColumn.IsVisible = !wide;
        WideColumns.IsVisible = wide;
    }

    private bool FourthStatShown() => _viewModel is { } vm && (vm.Funds?.HasFunds == true || vm.ShowBudgetCard);

    private void UpdateStats()
    {
        if (Bounds.Width <= 0) return;
        StatGrid.Columns = Bounds.Width < 620 ? 2 : FourthStatShown() ? 4 : 3;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WalletViewModel.ShowBudgetCard) or nameof(FundsViewModel.HasFunds):
                Dispatcher.UIThread.Post(UpdateStats);
                break;
            case nameof(WalletViewModel.FilterIndex):
                SyncFilter();
                break;
        }
    }

    private void OnToCategorizeChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.UIThread.Post(ShowToCategorize);

    /// <summary>The first few waiting transactions on the page; the button says how many more the sheet has.</summary>
    private void ShowToCategorize()
    {
        if (_viewModel is null) return;
        var waiting = _viewModel.ToCategorize;
        var shown = waiting.Take(ToCategorizeOnPage).ToList();
        if (ToCategorizeList.ItemsSource is not IReadOnlyList<TransactionRow> old || !old.SequenceEqual(shown)) ToCategorizeList.ItemsSource = shown;
        AllToCategorize.IsVisible = waiting.Count > shown.Count || _viewModel.HasMoreToCategorize;
        AllToCategorizeText.Text = _viewModel.HasMoreToCategorize ? "See all" : $"{waiting.Count - shown.Count} more";
    }

    private bool _syncing;

    private void Filter_Changed(object? sender, RoutedEventArgs e)
    {
        if (_syncing || _viewModel is null || sender is not RadioButton { IsChecked: true } button) return;
        _viewModel.FilterIndex = ReferenceEquals(button, FilterSpending) ? 1 : ReferenceEquals(button, FilterIncome) ? 2 : 0;
    }

    private void SyncFilter()
    {
        if (_viewModel is null) return;
        _syncing = true;
        (_viewModel.FilterIndex switch { 1 => FilterSpending, 2 => FilterIncome, _ => FilterAll }).IsChecked = true;
        _syncing = false;
    }
}
