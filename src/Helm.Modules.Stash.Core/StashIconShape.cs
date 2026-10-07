namespace Helm.Modules.Stash;

/// <summary>
/// The Stash icon, drawn the same on both platforms (each builds its own vector image from this): an arrow dropping
/// into a tray, in an indigo-to-lilac gradient, with no background. Path data is in a 100 × 100 box
/// (WPF/Avalonia syntax) and fills it edge to edge, like the Claude symbol, so every tool icon looks the same size.
/// </summary>
public static class StashIconShape
{
    public const double Size = 100;

    /// <summary>The tray, stroked with <see cref="Stroke"/>.</summary>
    public const string Tray = "M7 58H31L38 70H62L69 58H93V84A9 9 0 0 1 84 93H16A9 9 0 0 1 7 84Z";

    /// <summary>The arrow going in, stroked with <see cref="ArrowStroke"/> and round caps.</summary>
    public const string Arrow = "M50 7V46M32 29L50 47L68 29";

    /// <summary>Thick enough that the outline carries about the same ink as the other tools' icons.</summary>
    public const double Stroke = 10;

    public const double ArrowStroke = 10;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF4F46E5;

    public const uint GradientEnd = 0xFFC084FC;
}
