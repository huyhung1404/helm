namespace Helm.Modules.Notes;

/// <summary>
/// The Notes icon, drawn the same on both platforms (each builds its own vector image from this): a page with a folded
/// corner and three lines of text, in an orange-to-amber gradient, with no background. Path data is in a 100 × 100
/// box (WPF/Avalonia syntax) and fills it edge to edge, like the Claude symbol, so every tool icon looks the same size.
/// </summary>
public static class NotesIconShape
{
    public const double Size = 100;

    /// <summary>The page outline with its top-right corner cut off, stroked with <see cref="Stroke"/>.</summary>
    public const string Page =
        "M24 4H62L84 26V88A8 8 0 0 1 76 96H24A8 8 0 0 1 16 88V12A8 8 0 0 1 24 4Z";

    /// <summary>The folded corner, stroked with <see cref="Stroke"/>.</summary>
    public const string Fold = "M62 4V20A6 6 0 0 0 68 26H84";

    /// <summary>Three lines of text, the last one shorter, stroked with <see cref="LineStroke"/> and round caps.</summary>
    public const string Lines = "M32 46H68M32 62H68M32 78H54";

    public const double Stroke = 8;

    public const double LineStroke = 8;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFFEA580C;

    public const uint GradientEnd = 0xFFFBBF24;
}
