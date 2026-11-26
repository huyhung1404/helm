using Avalonia.Controls;

namespace Helm.Modules.Wallet.Ui;

/// <summary>The Debts tab of <see cref="WalletView"/>: the totals and the people (their sheet is <see cref="DebtSheet"/>).</summary>
public partial class DebtsView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public DebtsView() => InitializeComponent();

    public DebtsView(DebtsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
