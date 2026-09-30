using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.WatchLater;

/// <summary>Watch Later's icon as a WPF vector image (<see cref="WatchLaterIconShape"/>), used wherever the module appears.</summary>
public static class WatchLaterLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages, which may live on different windows.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = WatchLaterIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right.
        var gradient = new LinearGradientBrush(Argb(WatchLaterIconShape.GradientStart), Argb(WatchLaterIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        Pen Stroke(double width) => new(gradient, width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(new GeometryDrawing(null, Stroke(WatchLaterIconShape.Stroke), Geometry.Parse(WatchLaterIconShape.Ribbon)));
        group.Children.Add(new GeometryDrawing(gradient, Stroke(WatchLaterIconShape.PlayStroke), Geometry.Parse(WatchLaterIconShape.Play)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
