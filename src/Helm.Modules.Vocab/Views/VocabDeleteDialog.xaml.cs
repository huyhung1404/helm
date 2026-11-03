using System.Windows;
using System.Windows.Controls;

namespace Helm.Modules.Vocab.Views;

/// <summary>The opened word's Delete…, asked over the page (see the XAML).</summary>
public partial class VocabDeleteDialog : UserControl
{
    public VocabDeleteDialog() => InitializeComponent();

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Raised once it closes, deleted or not (the page gives the keys back).</summary>
    public event EventHandler? Closed;

    public void Open()
    {
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => CancelButton.Focus());
    }

    public void Close()
    {
        if (!IsOpen) return;
        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        (DataContext as VocabViewModel)?.DeleteWordConfirmedCommand.Execute(null);
        Close();
    }
}
