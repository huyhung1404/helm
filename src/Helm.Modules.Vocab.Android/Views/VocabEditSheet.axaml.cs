using Avalonia.Controls;

namespace Helm.Modules.Vocab;

/// <summary>The full-height sheet that edits a word by hand; DataContext: <see cref="VocabWordDetailViewModel"/> (its <c>Editor</c>).</summary>
public partial class VocabEditSheet : UserControl
{
    public VocabEditSheet()
    {
        InitializeComponent();
    }
}
