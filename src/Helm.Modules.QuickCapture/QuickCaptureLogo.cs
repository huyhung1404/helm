using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.QuickCapture;

/// <summary>The Quick Capture icon as a WPF vector image (<see cref="QuickCaptureIconShape"/>).</summary>
public static class QuickCaptureLogo
{
    /// <summary>Frozen: shared by the navigation, Home, the pages and the capture box.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = QuickCaptureIconShape.Size;
        var gradient = new LinearGradientBrush(Argb(QuickCaptureIconShape.GradientStart), Argb(QuickCaptureIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        var pen = new Pen(gradient, QuickCaptureIconShape.Stroke) { LineJoin = PenLineJoin.Round };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(new GeometryDrawing(gradient, pen, Geometry.Parse(QuickCaptureIconShape.Bolt)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
