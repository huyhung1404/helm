using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.Stash;

/// <summary>Stash's icon as a WPF vector image (<see cref="StashIconShape"/>), used wherever the module appears.</summary>
public static class StashLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages, which may live on different windows.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = StashIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right.
        var gradient = new LinearGradientBrush(Argb(StashIconShape.GradientStart), Argb(StashIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        Pen Stroke(double width) => new(gradient, width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(new GeometryDrawing(null, Stroke(StashIconShape.Stroke), Geometry.Parse(StashIconShape.Tray)));
        group.Children.Add(new GeometryDrawing(null, Stroke(StashIconShape.ArrowStroke), Geometry.Parse(StashIconShape.Arrow)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
