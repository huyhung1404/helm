namespace Helm.Modules.Keyboard;

/// <summary>
/// The Helm Keyboard icon: a keycap with an Â on it (the circumflex of Vietnamese), in a red-to-amber gradient, with no
/// background. Path data is in a 100 × 100 box and the keycap fills it edge to edge, like the other tool icons. PC only,
/// so the shape lives in the PC project.
/// </summary>
public static class KeyboardIconShape
{
    public const double Size = 100;

    /// <summary>The keycap, stroked with <see cref="Stroke"/>.</summary>
    public const string Keycap = "M20 10H80A16 16 0 0 1 96 26V74A16 16 0 0 1 80 90H20A16 16 0 0 1 4 74V26A16 16 0 0 1 20 10Z";

    /// <summary>The A and its circumflex, stroked with <see cref="Stroke"/> and round ends.</summary>
    public const string Letter = "M32 76L50 40L68 76M38.5 64H61.5M40 31L50 22L60 31";

    public const double Stroke = 8;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFFEF4444;

    public const uint GradientEnd = 0xFFF59E0B;
}
