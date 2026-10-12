namespace Helm.Modules.ShortcutGuide;

/// <summary>
/// The Shortcut Guide icon: a keycap with a sparkle on it (a hint), in a green-to-blue gradient, with no background.
/// Path data is in a 100 × 100 box and the keycap fills it edge to edge, like the other tool icons, so every tool
/// icon looks the same size. PC only, so the shape lives in the PC project.
/// </summary>
public static class ShortcutGuideIconShape
{
    public const double Size = 100;

    /// <summary>The keycap, stroked with <see cref="Stroke"/>.</summary>
    public const string Keycap = "M20 6H80A16 16 0 0 1 96 22V78A16 16 0 0 1 80 94H20A16 16 0 0 1 4 78V22A16 16 0 0 1 20 6Z";

    /// <summary>The four-pointed sparkle in the middle, filled.</summary>
    public const string Sparkle = "M50 26C52.5 41 59 47.5 74 50C59 52.5 52.5 59 50 74C47.5 59 41 52.5 26 50C41 47.5 47.5 41 50 26Z";

    public const double Stroke = 8;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF10B981;

    public const uint GradientEnd = 0xFF0EA5E9;
}
