namespace Helm.Modules.WeeklyReport;

/// <summary>
/// The Weekly Report icon: a page (a thick rounded outline) with three rising bars of a chart on it, in a green-to-cyan
/// gradient with no background. The page with its stroke spans the 100 × 100 box from top to bottom, like the other
/// tool icons. PC only, so the shape lives in the PC project.
/// </summary>
public static class WeeklyReportIconShape
{
    public const double Size = 100;

    public const double Stroke = 8;

    /// <summary>The page's outline (centre line): x, y, width, height, corner radius; the stroke adds 4 on each side.</summary>
    public static readonly (double X, double Y, double Width, double Height, double Radius) Page = (18, 4, 64, 92, 13);

    /// <summary>The bars, filled: x, y, width, height (bottoms on one line).</summary>
    public static readonly (double X, double Y, double Width, double Height)[] Bars = [(32, 62, 8, 18), (46, 50, 8, 30), (60, 36, 8, 44)];

    /// <summary>Gradient from the bottom-left corner to the top-right corner of the box (ARGB).</summary>
    public const uint GradientStart = 0xFF10B981;

    public const uint GradientEnd = 0xFF22D3EE;
}
