namespace Helm.Modules.Habits;

/// <summary>
/// The Habits icon, drawn the same on both platforms (each builds its own vector image from this): a 3 × 3 corner of
/// the year grid whose green squares make a check mark, the rest faint, with no background. Squares are in a 100 × 100
/// box and fill it edge to edge, like the Claude symbol, so every tool icon looks the same size.
/// </summary>
public static class HabitsIconShape
{
    public const double Size = 100;

    /// <summary>Side of one square.</summary>
    public const double Cell = 28;

    /// <summary>Space between squares (3 × 28 + 2 × 8 = 100).</summary>
    public const double Gap = 8;

    public const double Radius = 7;

    /// <summary>
    /// Colour of each square, row by row (ARGB). The check mark (middle-left, bottom-middle, middle-right, top-right)
    /// goes from pale to deep green, the way a streak grows; the other squares are a faint grey that reads on both
    /// themes.
    /// </summary>
    public static readonly uint[] Colors =
    [
        Faint, Faint, 0xFF216E39,
        0xFF9BE9A8, Faint, 0xFF30A14E,
        Faint, 0xFF40C463, Faint,
    ];

    public const uint Faint = 0x558B8D98;

    /// <summary>The top-left corner of square <paramref name="index"/> (0-8, row by row).</summary>
    public static (double X, double Y) Origin(int index) => (index % 3 * (Cell + Gap), index / 3 * (Cell + Gap));
}
