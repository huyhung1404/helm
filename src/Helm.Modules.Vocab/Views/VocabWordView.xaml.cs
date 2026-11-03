using System.Windows;
using System.Windows.Controls;

namespace Helm.Modules.Vocab.Views;

/// <summary>A word's page on the Words tab (see the XAML).</summary>
public partial class VocabWordView : UserControl
{
    public VocabWordView() => InitializeComponent();

    /// <summary>Delete… was chosen: the page asks first, in its own dialog.</summary>
    public event EventHandler? DeleteAsked;

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteAsked?.Invoke(this, EventArgs.Empty);
}
