using Avalonia.Controls;

namespace Helm.Modules.Vocab;

/// <summary>A word's collapsible sections after "How it's built"; DataContext: <see cref="VocabWordInfo"/>.</summary>
public partial class VocabInfoView : UserControl
{
    public VocabInfoView()
    {
        InitializeComponent();
    }
}
