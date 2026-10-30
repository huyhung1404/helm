using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Helm.Modules.Vocab;

/// <summary>
/// <c>local:ToneText.Text="{Binding Phonetic}"</c> on a TextBlock: pinyin with each syllable in its tone's colour
/// (<see cref="PinyinTones"/>); any other text (IPA) as it is.
/// </summary>
public sealed class ToneText : AvaloniaObject
{
    // Tone 1 red, 2 green, 3 blue, 4 purple, neutral grey: the colours learners know from Pleco and most dictionaries.
    private static readonly IBrush[] ToneBrushes =
    [
        new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
        new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47)),
        new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
        new SolidColorBrush(Color.FromRgb(0xAB, 0x47, 0xBC)),
        new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
    ];

    public static readonly AttachedProperty<string> TextProperty =
        AvaloniaProperty.RegisterAttached<ToneText, TextBlock, string>("Text", "");

    static ToneText()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, e) => Apply(block, e.NewValue as string ?? ""));
    }

    public static string GetText(TextBlock block) => block.GetValue(TextProperty);

    public static void SetText(TextBlock block, string value) => block.SetValue(TextProperty, value);

    private static void Apply(TextBlock block, string text)
    {
        var inlines = new InlineCollection();
        foreach (var piece in PinyinTones.Split(text))
        {
            var run = new Run(piece.Text);
            if (piece.Tone is >= 1 and <= 5) run.Foreground = ToneBrushes[piece.Tone - 1];
            inlines.Add(run);
        }
        block.Inlines = inlines;
    }
}
