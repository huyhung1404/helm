namespace Helm.Core.Ui;

/// <summary>
/// Helm's full-window background, in the Helm icon's colours: a navy (or light) vertical gradient with two soft glows,
/// blue top left and teal bottom right, drifting slowly. The one definition both renderers read: the WPF shell paints
/// it behind the whole window (WindowBackdrop) and a shared Avalonia page paints the same thing (Backdrop) lined up
/// with the window, so the two meet without a seam. The drift is a function of the clock, so both move together.
/// </summary>
public static class BackdropSpec
{
    public readonly record struct Glow(uint Color, double CenterX, double CenterY, double Radius);

    public readonly record struct Look(uint Top, uint Bottom, Glow Blue, Glow Teal);

    // The Helm icon is mostly its dark graphite tile, colour only in the two bars: the same here. Graphite, with the
    // blue and teal as faint glows (about a third as strong as a first try that tinted the whole window blue).
    public static Look Dark { get; } = new(0xFF1D2027, 0xFF121419,
        new Glow(0x1F0A7CF2, 0.15, 0.0, 0.75), new Glow(0x2600CFC0, 0.88, 1.0, 0.7));

    public static Look Light { get; } = new(0xFFF7F8FA, 0xFFECEFF3,
        new Glow(0x2606AEFF, 0.15, 0.0, 0.75), new Glow(0x2E00CFC0, 0.88, 1.0, 0.7));

    /// <summary>How far the glows are from where they rest at <paramref name="milliseconds"/> (any shared clock), in px.</summary>
    public static (double BlueX, double BlueY, double TealX, double TealY) Drift(long milliseconds)
    {
        var t = milliseconds / 1000.0;
        return (60 * Math.Sin(t * 2 * Math.PI / 34), 40 * Math.Sin(t * 2 * Math.PI / 26),
            -50 * Math.Sin(t * 2 * Math.PI / 46), -30 * Math.Sin(t * 2 * Math.PI / 38));
    }

    /// <summary>How often a renderer moves the glows: slow enough to cost nothing, smooth enough at this speed.</summary>
    public static TimeSpan Tick { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How much bigger than the window a glow layer is on every side, so a drifting glow never shows its edge.</summary>
    public const double Bleed = 160;
}
