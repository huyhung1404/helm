using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Helm.Modules.Vocab;

/// <summary>
/// <c>local:ToneText.Text="{Binding Phonetic}"</c> on a TextBlock, or on a Span inside one (to share its line's
/// baseline): pinyin with each syllable in its tone's colour (<see cref="PinyinTones"/>); any other text (IPA) as it is.
/// </summary>
public static class ToneText
{
    // Tone 1 red, 2 green, 3 blue, 4 purple, neutral grey: the colours learners know from Pleco and most dictionaries,
    // in the theme's shade (VocabTone1-5 of the palette).
    private static readonly string[] ToneKeys = ["VocabTone1", "VocabTone2", "VocabTone3", "VocabTone4", "VocabTone5"];

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ToneText), new PropertyMetadata("", OnTextChanged));

    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);

    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        var inlines = o switch
        {
            TextBlock block => block.Inlines,
            Span span => span.Inlines,
            _ => null,
        };
        if (inlines is null) return;
        inlines.Clear();
        foreach (var piece in PinyinTones.Split(e.NewValue as string ?? ""))
        {
            var run = new Run(piece.Text);
            if (piece.Tone is >= 1 and <= 5) run.SetResourceReference(TextElement.ForegroundProperty, ToneKeys[piece.Tone - 1]);
            inlines.Add(run);
        }
    }
}
