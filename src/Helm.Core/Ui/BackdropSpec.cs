namespace Helm.Core.Ui;

/// <summary>
/// Helm's full-window background, drawn like the Helm icon: its dark graphite tile with the four colours of its bars
/// as faint glows, one in each corner (blue top left, sky bottom left, cyan top right, teal bottom right), drifting
/// slowly. The one definition both renderers read: the WPF shell paints it behind the whole window (WindowBackdrop)
/// and a shared Avalonia page paints the same thing (Backdrop) lined up with the window, so the two meet without a
/// seam. The drift is a function of the clock, so both move together.
/// </summary>
public static class BackdropSpec
{
    /// <summary>A glow: colour (ARGB), centre and radius as fractions of the window, and how it drifts.</summary>
    public readonly record struct Glow(uint Color, double CenterX, double CenterY, double Radius, double DriftX, double DriftY, double Seconds);

    public readonly record struct Look(uint Top, uint Bottom, IReadOnlyList<Glow> Glows);

    // The icon's corners, from the image: bars #0077F2 → #06AEFF (left) and #00C6E0 → #00CFC0 (right).
    private const uint Blue = 0x0077F2, Sky = 0x06AEFF, Cyan = 0x00C6E0, Teal = 0x00CFC0;

    private static Glow[] Corners(uint alpha) =>
    [
        new((alpha << 24) | Blue, 0.0, 0.0, 0.62, 60, 40, 34),
        new((alpha << 24) | Sky, 0.0, 1.0, 0.55, 50, -35, 41),
        new((alpha << 24) | Cyan, 1.0, 0.0, 0.55, -55, 35, 29),
        new((alpha << 24) | Teal, 1.0, 1.0, 0.62, -50, -40, 46),
    ];

    // Dark and quiet so the cards on it read clearly; the corners are a hint of colour, not a tint.
    public static Look Dark { get; } = new(0xFF15171C, 0xFF0B0C0F, Corners(0x24));

    public static Look Light { get; } = new(0xFFF7F8FA, 0xFFECEFF3, Corners(0x33));

    /// <summary>How far <paramref name="glow"/> is from where it rests at <paramref name="milliseconds"/> (a shared clock), in px.</summary>
    public static (double X, double Y) Drift(Glow glow, long milliseconds)
    {
        var phase = milliseconds / 1000.0 * 2 * Math.PI / glow.Seconds;
        return (glow.DriftX * Math.Sin(phase), glow.DriftY * Math.Sin(phase * 0.77 + 1));
    }

    /// <summary>How often a renderer moves the glows: slow enough to cost nothing, smooth enough at this speed.</summary>
    public static TimeSpan Tick { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How much bigger than the window a glow layer is on every side, so a drifting glow never shows its edge.</summary>
    public const double Bleed = 160;
}
