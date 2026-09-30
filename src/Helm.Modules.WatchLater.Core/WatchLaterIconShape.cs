namespace Helm.Modules.WatchLater;

/// <summary>
/// The Watch Later icon, drawn the same on both platforms (each builds its own vector image from this): a bookmark
/// ribbon with a play button in it, in a red-to-rose gradient, with no background. Path data is in a 100 × 100 box
/// (WPF/Avalonia syntax); with its stroke the ribbon spans the box from top to bottom, like the Claude symbol, so every
/// tool icon looks the same size.
/// </summary>
public static class WatchLaterIconShape
{
    public const double Size = 100;

    /// <summary>The bookmark outline (a notch at the bottom), stroked with <see cref="Stroke"/>.</summary>
    public const string Ribbon =
        "M28 5H72A8 8 0 0 1 80 13V90A3.5 3.5 0 0 1 74.4 92.8L50 74L25.6 92.8A3.5 3.5 0 0 1 20 90V13A8 8 0 0 1 28 5Z";

    /// <summary>The play triangle, filled and stroked with <see cref="PlayStroke"/> so its corners are round.</summary>
    public const string Play = "M41 29V57L63 43Z";

    public const double Stroke = 8;

    public const double PlayStroke = 6;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFFDC2626;

    public const uint GradientEnd = 0xFFFB7185;
}
