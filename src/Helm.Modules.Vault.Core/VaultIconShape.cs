namespace Helm.Modules.Vault;

/// <summary>
/// The Vault icon, drawn the same on both platforms (each builds its own vector image from this): a shield with a
/// keyhole, in a blue-to-teal gradient, with no background. Path data is in a 100 × 100 box (WPF/Avalonia syntax)
/// and fills its height (1 to 99), like the Claude symbol, so every tool icon looks the same size.
/// </summary>
public static class VaultIconShape
{
    public const double Size = 100;

    /// <summary>The shield outline, stroked (not filled) with <see cref="ShieldStroke"/>.</summary>
    public const string Shield = "M50 5.35C61.85 12.46 73.71 16.41 86.35 18.78V48.02C86.35 70.15 71.34 85.96 50 94.65C28.66 85.96 13.65 70.15 13.65 48.02V18.78C26.29 16.41 38.15 12.46 50 5.35Z";

    public const double ShieldStroke = 8.7;

    /// <summary>The keyhole: a circle merging into a tapered stem with rounded bottom corners, filled.</summary>
    public const string Keyhole = "M50 29.06A14.23 14.23 0 0 1 57.75 55.3L61.06 74.26A2.69 2.69 0 0 1 58.38 77.42H41.62A2.69 2.69 0 0 1 38.94 74.26L42.25 55.3A14.23 14.23 0 0 1 50 29.06Z";

    /// <summary>Gradient from the top-left corner to the bottom-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF1778F2;

    public const uint GradientEnd = 0xFF16C3B0;
}
