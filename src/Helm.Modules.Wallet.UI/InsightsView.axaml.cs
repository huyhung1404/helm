using Avalonia;
using Avalonia.Controls;

namespace Helm.Modules.Wallet.Ui;

/// <summary>The Insights tab of <see cref="WalletView"/>: the month's charts, two columns on a wide screen.</summary>
public partial class InsightsView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public InsightsView() => InitializeComponent();

    public InsightsView(WalletViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var wide = e.NewSize.Width >= 820;
        Grid.SetColumnSpan(LeftCards, wide ? 1 : 3);
        Grid.SetColumn(RightCards, wide ? 2 : 0);
        Grid.SetColumnSpan(RightCards, wide ? 1 : 3);
        Grid.SetRow(RightCards, wide ? 0 : 1);
        RightCards.Margin = wide ? default : new Thickness(0, 16, 0, 0);
        StatGrid.Columns = e.NewSize.Width < 620 ? 2 : 4;
    }
}
