using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Helm.Modules.Vocab;

/// <summary>
/// <c>local:ToneText.Text="{Binding Phonetic}"</c> on a TextBlock: pinyin with each syllable in its tone's colour
/// (<see cref="PinyinTones"/>); any other text (IPA) as it is.
/// </summary>
public static class ToneText
{
    // Tone 1 red, 2 green, 3 blue, 4 purple, neutral grey: the colours learners know from Pleco and most dictionaries.
    private static readonly Brush[] ToneBrushes =
    [
        Freeze(Color.FromRgb(0xE5, 0x39, 0x35)),
        Freeze(Color.FromRgb(0x43, 0xA0, 0x47)),
        Freeze(Color.FromRgb(0x1E, 0x88, 0xE5)),
        Freeze(Color.FromRgb(0xAB, 0x47, 0xBC)),
        Freeze(Color.FromRgb(0x9E, 0x9E, 0x9E)),
    ];

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ToneText), new PropertyMetadata("", OnTextChanged));

    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);

    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock block) return;
        block.Inlines.Clear();
        foreach (var piece in PinyinTones.Split(e.NewValue as string ?? ""))
        {
            var run = new Run(piece.Text);
            if (piece.Tone is >= 1 and <= 5) run.Foreground = ToneBrushes[piece.Tone - 1];
            block.Inlines.Add(run);
        }
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
