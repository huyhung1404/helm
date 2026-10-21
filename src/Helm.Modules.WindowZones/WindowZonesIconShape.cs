namespace Helm.Modules.WindowZones;

/// <summary>
/// The Window Zones icon: a screen split into zones, a tall one on the left and two stacked on the right, in a
/// purple-to-pink gradient, with no background. Path data is in a 100 × 100 box and the screen fills it edge to edge
/// (2 to 98 with the stroke), like the other tool icons, so every tool icon looks the same size. PC only, so the shape
/// lives in the PC project.
/// </summary>
public static class WindowZonesIconShape
{
    public const double Size = 100;

    /// <summary>The screen and the lines between its zones, stroked with <see cref="Stroke"/>.</summary>
    public const string Zones =
        "M18 6H82A12 12 0 0 1 94 18V82A12 12 0 0 1 82 94H18A12 12 0 0 1 6 82V18A12 12 0 0 1 18 6Z M42 6V94 M42 46H94";

    public const double Stroke = 8;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF9333EA;

    public const uint GradientEnd = 0xFFF472B6;
}
