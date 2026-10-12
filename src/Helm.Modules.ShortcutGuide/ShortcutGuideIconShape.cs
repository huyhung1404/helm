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
    public const string Keycap = "M20 10H80A16 16 0 0 1 96 26V74A16 16 0 0 1 80 90H20A16 16 0 0 1 4 74V26A16 16 0 0 1 20 10Z";

    /// <summary>The four-pointed sparkle in the middle, filled.</summary>
    public const string Sparkle = "M50 30C52 42.5 57.5 48 70 50C57.5 52 52 57.5 50 70C48 57.5 42.5 52 30 50C42.5 48 48 42.5 50 30Z";

    public const double Stroke = 8;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF10B981;

    public const uint GradientEnd = 0xFF0EA5E9;
}
