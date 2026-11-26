using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Helm.Modules.NovelReader.Ui;

/// <summary>
/// The colours of the novel's text, one set for a light page and one for a dark page: names, the sentence read aloud (or
/// held), the paragraph marked for reading by eye (two faint sentence shades, a violet for what is said, and readable
/// blue, orange and green for the subject, the verb and the object), and study mode's pinyin and Hán Việt rows.
/// </summary>
internal sealed class ReadingPalette
{
    private static readonly ReadingPalette Light = new("#12000000", "#1F2563EB", "#249333EA", "#1D4ED8", "#C2410C", "#15803D", "#8A5A00", "#0E7A6A",
        "#0E6E8C", "#3D0EA5E9");
    private static readonly ReadingPalette Dark = new("#1CFFFFFF", "#3060A5FA", "#3AC084FC", "#7AB8FF", "#FFA45C", "#5EDB8A", "#F0B75A", "#5FD6C1",
        "#5CCFEA", "#5506AEFF");

    private ReadingPalette(string band0, string band1, string dialogue, string subject, string verb, string @object, string pinyin, string hanViet,
        string name, string spoken)
    {
        Bands = [Brush(band0), Brush(band1)];
        Dialogue = Brush(dialogue);
        Subject = Brush(subject);
        Verb = Brush(verb);
        Object = Brush(@object);
        Pinyin = Brush(pinyin);
        HanViet = Brush(hanViet);
        Name = Brush(name);
        Spoken = Brush(spoken);
    }

    public IBrush[] Bands { get; }

    public IBrush Dialogue { get; }

    public IBrush Subject { get; }

    public IBrush Verb { get; }

    public IBrush Object { get; }

    /// <summary>Study mode's pinyin row (amber) and Hán Việt row (teal).</summary>
    public IBrush Pinyin { get; }

    public IBrush HanViet { get; }

    /// <summary>A name of a person, place or sect.</summary>
    public IBrush Name { get; }

    /// <summary>The sentence being read aloud, or held.</summary>
    public IBrush Spoken { get; }

    public static ReadingPalette For(ThemeVariant? theme) => theme == ThemeVariant.Dark ? Dark : Light;

    private static ImmutableSolidColorBrush Brush(string color) => new(Color.Parse(color));
}
