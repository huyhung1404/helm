namespace Helm.Modules.Tracker;

/// <summary>
/// The Tracker icon, drawn the same on both platforms (each builds its own vector image from this): a big tick whose
/// long stroke leaves a ring through a gap at the top right ("done"), in a blue-to-cyan gradient, with no background.
/// Path data is in a 100 × 100 box (WPF/Avalonia syntax) and fills it edge to edge (1 to 99), like the Claude symbol,
/// so every tool icon looks the same size.
/// </summary>
public static class TrackerIconShape
{
    public const double Size = 100;

    /// <summary>
    /// The ring: centre (50, 50), radius 44.5, open between -66° and -17° where the tick passes, stroked with
    /// <see cref="Stroke"/> and round caps.
    /// </summary>
    public const string Ring = "M92.56 36.99A44.5 44.5 0 1 1 68.10 9.35";

    /// <summary>The tick: short stroke down, long stroke up and out of the ring, stroked with <see cref="CheckStroke"/>.</summary>
    public const string Check = "M29 50L45 66L94 8";

    public const double Stroke = 10;

    public const double CheckStroke = 12;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF0B69E7;

    public const uint GradientEnd = 0xFF3AD8FD;
}
