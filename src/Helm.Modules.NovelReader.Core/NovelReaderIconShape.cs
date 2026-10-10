namespace Helm.Modules.NovelReader;

/// <summary>
/// The Novel Reader icon, drawn the same on every platform (each builds its own vector image from this): an open book
/// with lines of text on both pages, in an indigo-to-lavender gradient, with no background. Path data is in a 100 × 100
/// box (WPF/Avalonia syntax); with its stroke the book spans the box from side to side, like the Claude symbol, so
/// every tool icon looks the same size.
/// </summary>
public static class NovelReaderIconShape
{
    public const double Size = 100;

    /// <summary>Both pages and the spine, stroked with <see cref="Stroke"/>.</summary>
    public const string Book =
        "M50 22C40 13 24 10 6 12V80C24 78 40 81 50 90C60 81 76 78 94 80V12C76 10 60 13 50 22Z M50 22V90";

    /// <summary>Three lines of text on each page, stroked with <see cref="LineStroke"/>.</summary>
    public const string Lines =
        "M17 31C26 30 35 32 41 36 M17 45C26 44 35 46 41 50 M17 59C26 58 35 60 41 64 " +
        "M83 31C74 30 65 32 59 36 M83 45C74 44 65 46 59 50 M83 59C74 58 65 60 59 64";

    public const double Stroke = 8;

    public const double LineStroke = 6;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF4F46E5;

    public const uint GradientEnd = 0xFFA78BFA;
}
