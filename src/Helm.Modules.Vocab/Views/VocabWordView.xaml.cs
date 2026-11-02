using System.Windows;
using System.Windows.Controls;

namespace Helm.Modules.Vocab.Views;

/// <summary>A word's page on the Words tab (see the XAML).</summary>
public partial class VocabWordView : UserControl
{
    public VocabWordView() => InitializeComponent();

    /// <summary>Delete… asks first (the page's confirmation).</summary>
    private void Delete_Click(object sender, RoutedEventArgs e) => (DataContext as VocabViewModel)?.DeleteWordCommand.Execute(null);
}
