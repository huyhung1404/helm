namespace Helm.Modules.Workspace;

/// <summary>
/// The Workspace Controller icon: a phone sending waves to the right (the remote control), in an orange-to-pink
/// gradient, with no background. Path data is in a 100 × 100 box; the phone spans it top to bottom and the outer wave
/// reaches the right edge, like the other tool icons. Shared, so the phone app draws the same icon.
/// </summary>
public static class WorkspaceIconShape
{
    public const double Size = 100;

    /// <summary>The phone, stroked with <see cref="Stroke"/>.</summary>
    public const string Phone = "M16 4H42A12 12 0 0 1 54 16V84A12 12 0 0 1 42 96H16A12 12 0 0 1 4 84V16A12 12 0 0 1 16 4Z";

    /// <summary>The home bar at the bottom of the screen, filled.</summary>
    public const string HomeBar = "M21 76H37A4 4 0 0 1 37 84H21A4 4 0 0 1 21 76Z";

    /// <summary>The near wave, stroked with round ends.</summary>
    public const string WaveNear = "M68.7 35.3A20 20 0 0 1 68.7 64.7";

    /// <summary>The far wave, stroked with round ends.</summary>
    public const string WaveFar = "M80.9 23.1A38 38 0 0 1 80.9 76.9";

    public const double Stroke = 8;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFFF97316;

    public const uint GradientEnd = 0xFFDB2777;
}
