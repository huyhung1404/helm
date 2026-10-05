namespace Helm.Core.Settings;

/// <summary>The background of an Android home-screen widget (Tracker, Wallet).</summary>
public enum WidgetBackground
{
    /// <summary>The app's card colour (light or dark with the phone's theme).</summary>
    Card,
    Light,
    Dark,
    Accent,

    /// <summary>The phone theme's card colour, see-through at the chosen opacity, so the text keeps its contrast.</summary>
    Transparent,
}

public enum WidgetText
{
    /// <summary>Readable on the background (dark text on light, light text on dark).</summary>
    Automatic,
    Dark,
    Light,
}

/// <summary>
/// A widget's colours as ARGB, from its settings: the background with its opacity, and the text. Null means "the
/// theme's colour resource" (light or dark with the phone), which the platform resolves.
/// </summary>
public readonly record struct WidgetLook(WidgetBackground Background, int Opacity, WidgetText Text)
{
    /// <summary>Where the opacity starts when the user picks <see cref="WidgetBackground.Transparent"/> from a solid one.</summary>
    public const int TransparentOpacity = 35;

    public static IReadOnlyList<string> BackgroundNames { get; } =
        ["Card (follows the theme)", "Light", "Dark", "Accent", "Transparent (follows the theme)"];

    public static IReadOnlyList<string> TextNames { get; } = ["Automatic", "Dark", "Light"];

    /// <summary>The background colour (alpha left solid; see <see cref="Alpha"/>); null for the theme's card colour.</summary>
    public uint? BackgroundColor => Background switch
    {
        WidgetBackground.Light => 0xFFFBFBFB,
        WidgetBackground.Dark => 0xFF202020,
        WidgetBackground.Accent => 0xFF0067C0,
        _ => null,
    };

    /// <summary>0-255 opacity of the background layer.</summary>
    public int Alpha => Byte(Opacity);

    /// <summary>True for light text, false for dark, null when the phone's theme decides (on the theme's own colour).</summary>
    public bool? LightText => Text switch
    {
        WidgetText.Light => true,
        WidgetText.Dark => false,
        _ => Background switch
        {
            WidgetBackground.Dark or WidgetBackground.Accent => true,
            WidgetBackground.Light => false,
            _ => null,
        },
    };

    /// <summary>Main and secondary text colours; null for the theme's.</summary>
    public (uint Main, uint Secondary)? TextColors => LightText switch
    {
        true => (0xFFFFFFFF, 0xCCFFFFFF),
        false => (0xFF1A1A1A, 0xFF5F5F5F),
        _ => null,
    };

    /// <summary>
    /// The opacity after the background changes: picking Transparent from a solid background starts see-through
    /// (a fully clear widget put dark text straight on the wallpaper).
    /// </summary>
    public static int OpacityAfter(WidgetBackground previous, WidgetBackground next, int opacity) =>
        next == WidgetBackground.Transparent && previous != WidgetBackground.Transparent && opacity > 60 ? TransparentOpacity : opacity;

    /// <summary>0-100 % as 0-255, rounded (integer maths: 50 % is 128).</summary>
    private static int Byte(int opacity) => (Math.Clamp(opacity, 0, 100) * 255 + 50) / 100;
}
