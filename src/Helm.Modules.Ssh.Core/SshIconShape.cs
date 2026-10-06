namespace Helm.Modules.Ssh;

/// <summary>
/// The SSH icon, drawn the same on both platforms (each builds its own vector image from this): a terminal window with
/// a prompt (a chevron and a cursor), in a teal-to-green gradient, with no background. Path data is in a 100 × 100 box
/// (WPF/Avalonia syntax) and fills it edge to edge, like the Claude symbol, so every tool icon looks the same size.
/// </summary>
public static class SshIconShape
{
    public const double Size = 100;

    /// <summary>The window outline, stroked with <see cref="Stroke"/>; 4..96 plus half the stroke spans the box.</summary>
    public const string Window = "M16 14H84A12 12 0 0 1 96 26V74A12 12 0 0 1 84 86H16A12 12 0 0 1 4 74V26A12 12 0 0 1 16 14Z";

    /// <summary>The prompt's chevron, stroked with <see cref="Stroke"/> and round joins.</summary>
    public const string Chevron = "M24 36L40 50L24 64";

    /// <summary>The cursor after the prompt.</summary>
    public const string Cursor = "M50 64H74";

    public const double Stroke = 8;

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF0F766E;

    public const uint GradientEnd = 0xFF4ADE80;
}
