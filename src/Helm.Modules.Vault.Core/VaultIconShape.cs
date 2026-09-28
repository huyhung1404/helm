namespace Helm.Modules.Vault;

/// <summary>
/// The Vault icon, drawn the same on both platforms (each builds its own vector image from this): a shield with a
/// keyhole on a rounded square with a blue-to-teal gradient. Path data is in a 100 × 100 box (WPF/Avalonia syntax).
/// </summary>
public static class VaultIconShape
{
    public const double Size = 100;

    /// <summary>Rounded square, 6 px margin, 22 px corners.</summary>
    public const string Background = "M28 6H72A22 22 0 0 1 94 28V72A22 22 0 0 1 72 94H28A22 22 0 0 1 6 72V28A22 22 0 0 1 28 6Z";

    /// <summary>The shield outline, stroked (not filled) with <see cref="ShieldStroke"/>.</summary>
    public const string Shield = "M50 23C57.5 27.5 65 30 73 31.5V50C73 64 63.5 74 50 79.5C36.5 74 27 64 27 50V31.5C35 30 42.5 27.5 50 23Z";

    public const double ShieldStroke = 5.5;

    /// <summary>The keyhole: a circle merging into a tapered stem with rounded bottom corners, filled.</summary>
    public const string Keyhole = "M50 38A9 9 0 0 1 54.9 54.6L57 66.6A1.7 1.7 0 0 1 55.3 68.6H44.7A1.7 1.7 0 0 1 43 66.6L45.1 54.6A9 9 0 0 1 50 38Z";

    /// <summary>Gradient from the top-left corner to the bottom-right corner (ARGB).</summary>
    public const uint GradientStart = 0xFF1778F2;

    public const uint GradientEnd = 0xFF16C3B0;

    public const uint Foreground = 0xFFFFFFFF;
}
