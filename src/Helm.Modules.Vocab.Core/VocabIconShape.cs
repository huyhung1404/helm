namespace Helm.Modules.Vocab;

/// <summary>
/// The Vocabulary icon, drawn the same on both platforms (each builds its own vector image from this): a small tree, one
/// dot branching into two, for a word breaking into its parts, in an amber-to-rose gradient with no background. Path
/// data is in a 100 × 100 box (WPF/Avalonia syntax) and fills it edge to edge, like the Claude symbol.
/// </summary>
public static class VocabIconShape
{
    public const double Size = 100;

    /// <summary>The three dots (filled): the word at the top, its two parts below.</summary>
    public const string Dots =
        "M36 15a14 14 0 1 0 28 0a14 14 0 1 0 -28 0Z " +
        "M2 84a14 14 0 1 0 28 0a14 14 0 1 0 -28 0Z " +
        "M70 84a14 14 0 1 0 28 0a14 14 0 1 0 -28 0Z";

    /// <summary>The two branches, stroked with <see cref="Stroke"/> and round caps (their ends hide under the dots).</summary>
    public const string Branches = "M50 15L16 84M50 15L84 84";

    public const double Stroke = 11;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFFF59E0B;

    public const uint GradientEnd = 0xFFEC4899;
}
