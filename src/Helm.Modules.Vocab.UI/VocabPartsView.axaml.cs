using Avalonia.Controls;

namespace Helm.Modules.Vocab.Ui;

/// <summary>How a word is built (see the XAML); DataContext: <see cref="VocabWordDetailViewModel"/>.</summary>
public partial class VocabPartsView : UserControl
{
    /// <summary>Below this the wide tree would scroll sideways: the outline shows instead.</summary>
    public const double TreeWidth = 520;

    private const string Hint = "Tap a part to see what it means and which of your words share it.";

    public VocabPartsView() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var outline = e.NewSize.Width < TreeWidth;
        TreeScroller.IsVisible = !outline;
        Outline.IsVisible = outline;
        // The tree's boxes have a right-click menu; on the outline the part's card has the button.
        PickHint.Text = outline ? Hint : Hint + " Right-click a character or an inner word to add it as a word.";
    }
}
