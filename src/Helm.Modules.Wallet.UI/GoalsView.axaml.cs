using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Helm.Core.Ui;

namespace Helm.Modules.Wallet.Ui;

/// <summary>The Goals tab of <see cref="WalletView"/>: the balance left to spend and the goals (their sheet is <see cref="GoalSheet"/>).</summary>
public partial class GoalsView : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public GoalsView() => InitializeComponent();

    public GoalsView(GoalsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Goals two by two on a phone, three across a wide page; the set-aside line under the balance on a phone.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < 620;
        var columns = e.NewSize.Width < 860 ? 2 : 3;
        if (GoalList.ItemsPanelRoot is UniformGrid grid) grid.Columns = columns;
        else GoalList.ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Columns = columns });
        FirstRunSteps.Columns = narrow ? 1 : 3;
        Grid.SetColumn(SetAsideLine, narrow ? 0 : 1);
        Grid.SetRow(SetAsideLine, narrow ? 1 : 0);
        SetAsideLine.Margin = narrow ? new Avalonia.Thickness(0, 10, 0, 0) : new Avalonia.Thickness(16, 0, 0, 0);
        SetAsideLine.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
    }

    private void ToggleFinished_Click(object? sender, RoutedEventArgs e)
    {
        var open = !FinishedList.IsVisible;
        Motion.SetTurn(FinishedChevron, open);
        FinishedList.IsVisible = open;
    }
}
