using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.SlackBot;

/// <summary>The Slack Bot icon as a WPF vector image (<see cref="SlackBotIconShape"/>).</summary>
public static class SlackBotLogo
{
    /// <summary>Frozen: shared by the navigation, Home, the pages and the overlay.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        static Geometry Box((double X, double Y, double Width, double Height, double Radius) b) =>
            new RectangleGeometry(new Rect(b.X, b.Y, b.Width, b.Height), b.Radius, b.Radius);
        const double size = SlackBotIconShape.Size;
        var gradient = new LinearGradientBrush(Argb(SlackBotIconShape.GradientStart), Argb(SlackBotIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        var pen = new Pen(gradient, SlackBotIconShape.Stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        foreach (var ear in SlackBotIconShape.Ears) group.Children.Add(new GeometryDrawing(gradient, null, Box(ear)));
        group.Children.Add(new GeometryDrawing(null, pen, Box(SlackBotIconShape.Head)));
        var (x, top, bottom) = SlackBotIconShape.Stalk;
        group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(x, top), new Point(x, bottom))));
        var ball = SlackBotIconShape.Ball;
        group.Children.Add(new GeometryDrawing(gradient, null, new EllipseGeometry(new Point(ball.X, ball.Y), ball.R, ball.R)));
        foreach (var (ex, ey, r) in SlackBotIconShape.Eyes)
            group.Children.Add(new GeometryDrawing(gradient, null, new EllipseGeometry(new Point(ex, ey), r, r)));
        group.Children.Add(new GeometryDrawing(gradient, null, Box(SlackBotIconShape.Mouth)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
