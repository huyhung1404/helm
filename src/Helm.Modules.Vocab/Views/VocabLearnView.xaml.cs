using System.Windows;
using System.Windows.Controls;

namespace Helm.Modules.Vocab.Views;

/// <summary>The Learn tab (see the XAML). Its keys are the page's (<see cref="VocabContentPage"/>).</summary>
public partial class VocabLearnView : UserControl
{
    /// <summary>Below this the card beside the side panel is too narrow for its answer: the panel folds into a line.</summary>
    internal const double SidePanelWidth = 1060;

    /// <summary>Below this a tree beside what helps gets cut off: the tree goes above it, across the card.</summary>
    internal const double SideBySideWidth = 760;

    public VocabLearnView() => InitializeComponent();

    private VocabViewModel? ViewModel => DataContext as VocabViewModel;

    private void LearnPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= SidePanelWidth;
        SidePanel.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        NarrowBar.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        GapColumn.Width = new GridLength(wide ? 20 : 0);
        SideColumn.Width = new GridLength(wide ? 280 : 0);
    }

    /// <summary>
    /// Wide: the tree and what helps side by side (the styles decide who spans when one is missing). Narrow: both across
    /// the card, the tree first.
    /// </summary>
    private void AnswerGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width >= SideBySideWidth)
        {
            foreach (var part in new FrameworkElement[] { TreePart, InfoPart })
            {
                part.ClearValue(Grid.RowProperty);
                part.ClearValue(Grid.ColumnProperty);
                part.ClearValue(Grid.ColumnSpanProperty);
                part.ClearValue(MarginProperty);
            }
            return;
        }
        Grid.SetColumn(TreePart, 0);
        Grid.SetColumnSpan(TreePart, 3);
        Grid.SetRow(InfoPart, 1);
        Grid.SetColumn(InfoPart, 0);
        Grid.SetColumnSpan(InfoPart, 3);
        InfoPart.Margin = new Thickness(0, TreePart.Visibility == Visibility.Visible ? 20 : 0, 0, 0);
    }

    /// <summary>The card's word, open on the Words tab with its whole tree.</summary>
    private void OpenWord_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { Card.WordId: { } id } vm) vm.Open(id);
    }

    /// <summary>The card's word in the Edit dialog, over the card: the Learn tab stays in front.</summary>
    private void EditWord_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { Card.WordId: { } id } vm) return;
        var tab = vm.Tab;
        vm.Open(id);
        vm.Tab = tab;
        vm.Detail?.Editor.BeginCommand.Execute(null);
    }
}
