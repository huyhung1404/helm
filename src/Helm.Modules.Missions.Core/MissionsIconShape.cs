namespace Helm.Modules.Missions;

/// <summary>
/// The Missions icon, drawn the same on both platforms (each builds its own vector image from this): a flag on a pole
/// at the top of a winding path, in an amber-to-orange gradient, with no background. Path data is in a 100 × 100 box
/// (WPF/Avalonia syntax); with its strokes it spans the box from top to bottom, like the Claude symbol, so every tool
/// icon looks the same size.
/// </summary>
public static class MissionsIconShape
{
    public const double Size = 100;

    /// <summary>The pole, stroked with <see cref="Stroke"/> and round caps.</summary>
    public const string Pole = "M60 6V60";

    /// <summary>The flag, filled and stroked with <see cref="FlagStroke"/> so its corners are round.</summary>
    public const string Flag = "M60 8H90L80 21L90 34H60Z";

    /// <summary>The road up to the pole: from the bottom-left corner, one bend, to the foot of the pole.</summary>
    public const string Path = "M6 94C26 94 30 76 46 76C58 76 60 68 60 60";

    /// <summary>A dot where the road begins.</summary>
    public const string Start = "M6 94m-6 0a6 6 0 1 0 12 0a6 6 0 1 0 -12 0";

    public const double Stroke = 9;

    public const double FlagStroke = 6;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFFEA580C;

    public const uint GradientEnd = 0xFFFBBF24;
}
