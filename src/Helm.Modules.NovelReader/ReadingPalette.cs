using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.NovelReader;

/// <summary>
/// The colours of a paragraph marked for reading by eye (<see cref="SegmentText"/>), one set for a light page and one for
/// a dark page: two faint sentence shades, a violet for what is said, and readable blue, orange and green for the
/// subject, the verb and the object. The page is dark when its text is light.
/// </summary>
internal sealed class ReadingPalette
{
    private static readonly ReadingPalette Light = new("#12000000", "#1F2563EB", "#249333EA", "#1D4ED8", "#C2410C", "#15803D", "#8A5A00", "#0E7A6A");
    private static readonly ReadingPalette Dark = new("#1CFFFFFF", "#3060A5FA", "#3AC084FC", "#7AB8FF", "#FFA45C", "#5EDB8A", "#F0B75A", "#5FD6C1");

    private ReadingPalette(string band0, string band1, string dialogue, string subject, string verb, string @object, string pinyin, string hanViet)
    {
        Bands = [Brush(band0), Brush(band1)];
        Dialogue = Brush(dialogue);
        Subject = Brush(subject);
        Verb = Brush(verb);
        Object = Brush(@object);
        Pinyin = Brush(pinyin);
        HanViet = Brush(hanViet);
    }

    /// <summary>Study mode's pinyin row (amber) and Hán Việt row (teal).</summary>
    public Brush Pinyin { get; }

    public Brush HanViet { get; }

    public Brush[] Bands { get; }

    public Brush Dialogue { get; }

    public Brush Subject { get; }

    public Brush Verb { get; }

    public Brush Object { get; }

    public static ReadingPalette For(FrameworkElement element)
    {
        var text = element.TryFindResource("TextFillColorPrimaryBrush") as SolidColorBrush;
        var c = text?.Color ?? Colors.Black;
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        return luminance > 0.5 ? Dark : Light;
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
