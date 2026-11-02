using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Helm.Modules.Vocab;

/// <summary>The Words tab's list; DataContext: <see cref="VocabWordListViewModel"/>.</summary>
public partial class VocabWordsView : UserControl
{
    public VocabWordsView()
    {
        InitializeComponent();
    }

    private void Order_Click(object? sender, RoutedEventArgs e) => VocabSheets.Show(this, VocabSheet.Order);
}
