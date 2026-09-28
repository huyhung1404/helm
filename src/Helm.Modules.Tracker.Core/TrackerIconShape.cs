namespace Helm.Modules.Tracker;

/// <summary>
/// The Tracker icon, drawn the same on both platforms (each builds its own vector image from this): a checklist of
/// two ticked boxes and an open circle, in a blue-to-cyan gradient, with no background. Path data is in a
/// 100 × 100 box (WPF/Avalonia syntax) and fills it edge to edge, like the Claude symbol, so every tool icon looks
/// the same size.
/// </summary>
public static class TrackerIconShape
{
    public const double Size = 100;

    /// <summary>Two rounded boxes (rows 1 and 2) and a circle (row 3), stroked with <see cref="Stroke"/>.</summary>
    public const string Boxes =
        "M10.5 4.5H23.5A6 6 0 0 1 29.5 10.5V23.5A6 6 0 0 1 23.5 29.5H10.5A6 6 0 0 1 4.5 23.5V10.5A6 6 0 0 1 10.5 4.5Z" +
        "M10.5 37.5H23.5A6 6 0 0 1 29.5 43.5V56.5A6 6 0 0 1 23.5 62.5H10.5A6 6 0 0 1 4.5 56.5V43.5A6 6 0 0 1 10.5 37.5Z" +
        "M17 70.5A12.5 12.5 0 1 1 17 95.5A12.5 12.5 0 1 1 17 70.5Z";

    /// <summary>The ticks in the two boxes, stroked with <see cref="Stroke"/> and round caps.</summary>
    public const string Ticks = "M11.5 17.5L15.5 21.5L22.5 12.5M11.5 50.5L15.5 54.5L22.5 45.5";

    /// <summary>One line per row, stroked with <see cref="LineStroke"/> and round caps.</summary>
    public const string Lines = "M43 17H95M43 50H95M43 83H95";

    public const double Stroke = 8;

    public const double LineStroke = 9;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB), as in the original art.</summary>
    public const uint GradientStart = 0xFF0B69E7;

    public const uint GradientEnd = 0xFF3AD8FD;
}
