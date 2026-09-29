namespace Helm.Modules.CommandPalette;

/// <summary>
/// The Command Palette icon: a rounded command box with a prompt and a line of text, in a violet-to-pink gradient,
/// with no background. Path data is in a 100 × 100 box and its width fills it edge to edge (0 to 100), like the
/// Claude symbol, so every tool icon looks the same size. PC only, so the shape lives in the PC project.
/// </summary>
public static class CommandPaletteIconShape
{
    public const double Size = 100;

    /// <summary>The box, stroked with <see cref="Stroke"/>.</summary>
    public const string Box = "M18 16H82A14 14 0 0 1 96 30V70A14 14 0 0 1 82 84H18A14 14 0 0 1 4 70V30A14 14 0 0 1 18 16Z";

    /// <summary>The prompt "&gt;" and the typed text, stroked with <see cref="Stroke"/> and round caps.</summary>
    public const string Prompt = "M24 37L37 50L24 63M47 63H76";

    public const double Stroke = 9;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF7C3AED;

    public const uint GradientEnd = 0xFFEC4899;
}
