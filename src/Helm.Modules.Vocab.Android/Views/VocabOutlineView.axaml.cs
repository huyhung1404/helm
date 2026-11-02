using Avalonia;
using Avalonia.Controls;

namespace Helm.Modules.Vocab;

/// <summary>How a word is built (its outline and the tapped part); DataContext: <see cref="VocabWordDetailViewModel"/>.</summary>
public partial class VocabOutlineView : UserControl
{
    private const string PickText = "Tap a part to see what it means and which of your words share it.";

    /// <summary>The hint under the outline also says that holding a part adds it as a word (the word's page, not the Learn card).</summary>
    public static readonly StyledProperty<bool> ShowsHoldHintProperty = AvaloniaProperty.Register<VocabOutlineView, bool>(nameof(ShowsHoldHint), true);

    public VocabOutlineView()
    {
        InitializeComponent();
    }

    public bool ShowsHoldHint
    {
        get => GetValue(ShowsHoldHintProperty);
        set => SetValue(ShowsHoldHintProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowsHoldHintProperty)
            PickHint.Text = ShowsHoldHint ? PickText + " Hold a character or an inner word to add it as a word." : PickText;
    }
}
