using Avalonia.Controls;

namespace Helm.Modules.Vocab.Ui;

/// <summary>The Add text sheet (see the XAML); open while <see cref="VocabTextsViewModel.IsOpen"/>.</summary>
public partial class VocabTextsSheet : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public VocabTextsSheet() => InitializeComponent();

    public VocabTextsSheet(VocabViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>The cursor where the text goes first (its title, then the text).</summary>
    public void FocusFirst() => TitleBox.Focus();
}
