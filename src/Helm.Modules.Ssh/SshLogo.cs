using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.Ssh;

/// <summary>SSH's icon as a WPF vector image (<see cref="SshIconShape"/>), used wherever the module appears.</summary>
public static class SshLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages, which may live on different windows.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = SshIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right.
        var gradient = new LinearGradientBrush(Argb(SshIconShape.GradientStart), Argb(SshIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        var pen = new Pen(gradient, SshIconShape.Stroke) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(SshIconShape.Window)));
        group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(SshIconShape.Chevron)));
        group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(SshIconShape.Cursor)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
