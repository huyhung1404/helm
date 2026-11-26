using Avalonia.Controls;

namespace Helm.Modules.Vocab.Ui;

/// <summary>Ask for a new analysis (see the XAML); open while <see cref="VocabWordDetailViewModel.IsAskingRedo"/>.</summary>
public partial class VocabRedoSheet : UserControl
{
    /// <summary>For the XAML loader and the previewer only.</summary>
    public VocabRedoSheet() => InitializeComponent();

    public VocabRedoSheet(VocabWordDetailViewModel detail)
    {
        DataContext = detail;
        InitializeComponent();
    }

    public void FocusNote() => NoteBox.Focus();
}
