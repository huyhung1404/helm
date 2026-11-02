namespace Helm.Modules.SlackBot;

/// <summary>
/// The SlackBot icon: a speech bubble drawn as a thick outline with three dots inside (someone is writing), in a
/// violet-to-pink gradient with no background. The outline (with its stroke) spans the 100 × 100 box edge to edge,
/// like the other tool icons. PC only, so the shape lives in the PC project.
/// </summary>
public static class SlackBotIconShape
{
    public const double Size = 100;

    public const double Stroke = 8;

    /// <summary>The bubble's centre line; the stroke adds 4 on each side (0-100 across, 5-98 down).</summary>
    public const string Bubble =
        "M 24,9 H 76 A 20,20 0 0 1 96,29 V 56 A 20,20 0 0 1 76,76 H 44 L 24,94 V 76 A 20,20 0 0 1 4,56 V 29 A 20,20 0 0 1 24,9 Z";

    /// <summary>The three dots: centre x, centre y, radius.</summary>
    public static readonly (double X, double Y, double R)[] Dots = [(31, 43, 6), (50, 43, 6), (69, 43, 6)];

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF7C3AED;

    public const uint GradientEnd = 0xFFEC4899;
}
