using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.Missions;

/// <summary>Missions' icon as a WPF vector image (<see cref="MissionsIconShape"/>), used wherever the module appears.</summary>
public static class MissionsLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages, which may live on different windows.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = MissionsIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right.
        var gradient = new LinearGradientBrush(Argb(MissionsIconShape.GradientStart), Argb(MissionsIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        Pen Stroke(double width) => new(gradient, width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(new GeometryDrawing(null, Stroke(MissionsIconShape.Stroke), Geometry.Parse(MissionsIconShape.Path)));
        group.Children.Add(new GeometryDrawing(null, Stroke(MissionsIconShape.Stroke), Geometry.Parse(MissionsIconShape.Pole)));
        group.Children.Add(new GeometryDrawing(gradient, Stroke(MissionsIconShape.FlagStroke), Geometry.Parse(MissionsIconShape.Flag)));
        group.Children.Add(new GeometryDrawing(gradient, null, Geometry.Parse(MissionsIconShape.Start)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
