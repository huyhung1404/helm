using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Helm.Modules.Vocab;

/// <summary>The Learn tab: today's numbers and the card; DataContext: <see cref="VocabViewModel"/>.</summary>
public partial class VocabLearnView : UserControl
{
    public VocabLearnView()
    {
        InitializeComponent();
    }

    private void CardMenu_Click(object? sender, RoutedEventArgs e) => VocabSheets.Show(this, VocabSheet.CardMenu);
}
