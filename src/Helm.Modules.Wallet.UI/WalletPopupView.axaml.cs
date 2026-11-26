using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Helm.Modules.Wallet.Ui;

/// <summary>
/// The body of the Wallet sheet: the part for <see cref="WalletViewModel.Popup"/> (<see cref="TransactionSheet"/>,
/// <see cref="FundSheet"/>, <see cref="GoalSheet"/>, <see cref="DebtSheet"/>), the keyboard in its first box.
/// </summary>
public partial class WalletPopupView : UserControl
{
    private readonly WalletViewModel? _viewModel;
    private readonly TransactionSheet? _transactions;
    private readonly FundSheet? _funds;
    private readonly GoalSheet? _goals;
    private readonly DebtSheet? _debts;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public WalletPopupView() => InitializeComponent();

    public WalletPopupView(WalletViewModel viewModel, bool narrow)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Classes.Set("narrow", narrow);
        AmountGrouping.Attach(this);
        _transactions = new TransactionSheet(viewModel);
        Parts.Children.Add(_transactions);
        if (viewModel.Funds is { } funds) Parts.Children.Add(_funds = new FundSheet(funds));
        if (viewModel.Goals is { } goals)
        {
            Parts.Children.Add(_goals = new GoalSheet(goals));
            _goals.SetNarrow(narrow);
        }
        if (viewModel.Debts is { } debts) Parts.Children.Add(_debts = new DebtSheet(debts));
        AttachedToVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged += OnChanged;
            Apply();
        };
        DetachedFromVisualTree += (_, _) => viewModel.PropertyChanged -= OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WalletViewModel.Popup)) Apply();
    }

    /// <summary>Shows the part of the pop-up open now and puts the keyboard in its first box.</summary>
    private void Apply()
    {
        if (_viewModel is null) return;
        var popup = _viewModel.Popup;
        var funds = _funds?.Show(popup) == true;
        var goals = _goals?.Show(popup) == true;
        var debts = _debts?.Show(popup) == true;
        if (_transactions is not null) _transactions.IsVisible = !funds && !goals && !debts;
        if (_funds is not null) _funds.IsVisible = funds;
        if (_goals is not null) _goals.IsVisible = goals;
        if (_debts is not null) _debts.IsVisible = debts;
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.Popup != popup) return;
            var box = _transactions?.FirstBox(popup) ?? _funds?.FirstBox(popup) ?? _goals?.FirstBox(popup) ?? _debts?.FirstBox(popup);
            box?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Input);
    }
}
