using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.SlackBot;

/// <summary>The SlackBot icon as a WPF vector image (<see cref="SlackBotIconShape"/>).</summary>
public static class SlackBotLogo
{
    /// <summary>Frozen: shared by the navigation, Home, the pages and the overlay.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = SlackBotIconShape.Size;
        var gradient = new LinearGradientBrush(Argb(SlackBotIconShape.GradientStart), Argb(SlackBotIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        var pen = new Pen(gradient, SlackBotIconShape.Stroke) { LineJoin = PenLineJoin.Round };
        group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(SlackBotIconShape.Bubble)));
        foreach (var (x, y, r) in SlackBotIconShape.Dots)
            group.Children.Add(new GeometryDrawing(gradient, null, new EllipseGeometry(new Point(x, y), r, r)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
