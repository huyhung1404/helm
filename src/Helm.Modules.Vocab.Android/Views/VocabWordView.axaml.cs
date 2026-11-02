using Avalonia.Controls;

namespace Helm.Modules.Vocab;

/// <summary>A word's own page; DataContext: <see cref="VocabWordDetailViewModel"/>. Its app bar and sheets are the page's.</summary>
public partial class VocabWordView : UserControl
{
    public VocabWordView()
    {
        InitializeComponent();
    }
}
