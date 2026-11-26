using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.SlackBot.Ui;

/// <summary>The Slack Report icon as an Avalonia vector image (<see cref="SlackBotIconShape"/>).</summary>
public static class SlackBotIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Geometry Rounded((double X, double Y, double Width, double Height, double Radius) b) =>
            new RectangleGeometry(new Rect(b.X, b.Y, b.Width, b.Height), b.Radius, b.Radius);
        const double size = SlackBotIconShape.Size;
        var head = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromUInt32(SlackBotIconShape.HeadStart), 0), new GradientStop(Color.FromUInt32(SlackBotIconShape.HeadEnd), 1) },
        };
        var glow = new SolidColorBrush(Color.FromUInt32(SlackBotIconShape.GlowColor));
        var ears = new SolidColorBrush(Color.FromUInt32(SlackBotIconShape.EarColor));
        // A transparent box keeps the icon's own margins at any size.
        var group = new DrawingGroup
        {
            Children = { new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) } },
        };
        foreach (var ear in SlackBotIconShape.Ears) group.Children.Add(new GeometryDrawing { Brush = ears, Geometry = Rounded(ear) });
        group.Children.Add(new GeometryDrawing { Pen = new Pen(head, SlackBotIconShape.Stroke), Geometry = Rounded(SlackBotIconShape.Head) });
        var (sx, top, bottom) = SlackBotIconShape.Stalk;
        group.Children.Add(new GeometryDrawing { Pen = new Pen(glow, SlackBotIconShape.Stroke, lineCap: PenLineCap.Round), Geometry = new LineGeometry(new Point(sx, top), new Point(sx, bottom)) });
        var ball = SlackBotIconShape.Ball;
        group.Children.Add(new GeometryDrawing { Brush = glow, Geometry = new EllipseGeometry(new Rect(ball.X - ball.R, ball.Y - ball.R, ball.R * 2, ball.R * 2)) });
        foreach (var (ex, ey, er) in SlackBotIconShape.Eyes)
            group.Children.Add(new GeometryDrawing { Brush = glow, Geometry = new EllipseGeometry(new Rect(ex - er, ey - er, er * 2, er * 2)) });
        group.Children.Add(new GeometryDrawing { Brush = glow, Geometry = Rounded(SlackBotIconShape.Mouth) });
        return new DrawingImage(group);
    }
}
