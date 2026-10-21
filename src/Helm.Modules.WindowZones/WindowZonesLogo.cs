using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.WindowZones;

/// <summary>The Window Zones icon as a WPF vector image (<see cref="WindowZonesIconShape"/>).</summary>
public static class WindowZonesLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages, which may live on different windows.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = WindowZonesIconShape.Size;
        var gradient = new LinearGradientBrush(Argb(WindowZonesIconShape.GradientStart), Argb(WindowZonesIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        var pen = new Pen(gradient, WindowZonesIconShape.Stroke) { LineJoin = PenLineJoin.Round };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(WindowZonesIconShape.Zones)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
