using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace Helm.Modules.Vocab;

/// <summary>
/// <c>local:GapText.Text="{Binding FrontText}"</c> on a TextBlock: a Fill the gap sentence with its gap
/// (<see cref="VocabCloze.Gap"/>) drawn as the design's blank, a 96 px accent line on the text's baseline. Other text
/// is shown as it is.
/// </summary>
public sealed class GapText : AvaloniaObject
{
    public static readonly AttachedProperty<string> TextProperty =
        AvaloniaProperty.RegisterAttached<GapText, TextBlock, string>("Text", "");

    static GapText()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, e) => Apply(block, e.NewValue as string ?? ""));
    }

    public static string GetText(TextBlock block) => block.GetValue(TextProperty);

    public static void SetText(TextBlock block, string value) => block.SetValue(TextProperty, value);

    private static void Apply(TextBlock block, string text)
    {
        var inlines = new InlineCollection();
        var pieces = text.Split(VocabCloze.Gap);
        for (var i = 0; i < pieces.Length; i++)
        {
            if (i > 0)
            {
                var blank = new Border { Width = 96, Height = 2, Margin = new Thickness(4, 0) };
                blank.Bind(Border.BackgroundProperty, block.GetResourceObservable("VocabAccent"));
                inlines.Add(new InlineUIContainer(blank));
            }
            if (pieces[i].Length > 0) inlines.Add(new Run(pieces[i]));
        }
        block.Inlines = inlines;
    }
}
