using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.WeeklyReport;

/// <summary>The Weekly Report icon as a WPF vector image (<see cref="WeeklyReportIconShape"/>).</summary>
public static class WeeklyReportLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = WeeklyReportIconShape.Size;
        var gradient = new LinearGradientBrush(Argb(WeeklyReportIconShape.GradientStart), Argb(WeeklyReportIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        var (x, y, w, h, r) = WeeklyReportIconShape.Page;
        group.Children.Add(new GeometryDrawing(null, new Pen(gradient, WeeklyReportIconShape.Stroke), new RectangleGeometry(new Rect(x, y, w, h), r, r)));
        foreach (var (bx, by, bw, bh) in WeeklyReportIconShape.Bars)
            group.Children.Add(new GeometryDrawing(gradient, null, new RectangleGeometry(new Rect(bx, by, bw, bh), 3, 3)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
