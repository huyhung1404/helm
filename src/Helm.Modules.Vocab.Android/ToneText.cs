using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace Helm.Modules.Vocab;

/// <summary>
/// <c>local:ToneText.Text="{Binding Phonetic}"</c> on a TextBlock: pinyin with each syllable in its tone's colour
/// (<see cref="PinyinTones"/>); any other text (IPA) in the secondary colour. The colours are VocabStyles' theme
/// resources (VocabTone1-5), lighter in dark mode.
/// </summary>
public sealed class ToneText : AvaloniaObject
{
    // Tone 1 red, 2 green, 3 blue, 4 purple, neutral grey: the colours learners know from Pleco and most dictionaries.
    private static readonly string[] ToneKeys = ["VocabTone1", "VocabTone2", "VocabTone3", "VocabTone4", "VocabTone5"];

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
            // A syllable in its tone's colour; text without tones (IPA) is the secondary colour, like the tone-less syllables.
            var key = piece.Tone is >= 1 and <= 5 ? ToneKeys[piece.Tone - 1] : piece.Text.Trim().Length > 0 ? "VocabTone5" : null;
            if (key is not null) run.Bind(TextElement.ForegroundProperty, block.GetResourceObservable(key));
            inlines.Add(run);
        }
        block.Inlines = inlines;
    }
}
