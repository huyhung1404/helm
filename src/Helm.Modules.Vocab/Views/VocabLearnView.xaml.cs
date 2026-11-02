using System.Windows;
using System.Windows.Controls;

namespace Helm.Modules.Vocab.Views;

/// <summary>The Learn tab (see the XAML). Its keys are the page's (<see cref="VocabContentPage"/>).</summary>
public partial class VocabLearnView : UserControl
{
    public VocabLearnView() => InitializeComponent();

    private VocabViewModel? ViewModel => DataContext as VocabViewModel;

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
