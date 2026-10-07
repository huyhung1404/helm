using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.Stash;

/// <summary>The Stash icon as an Avalonia vector image, drawn from the same shapes as on Windows.</summary>
public static class StashIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        const double size = StashIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right.
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromUInt32(StashIconShape.GradientStart), 0), new GradientStop(Color.FromUInt32(StashIconShape.GradientEnd), 1) },
        };
        Pen Stroke(double width) => new(gradient, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var group = new DrawingGroup
        {
            Children =
            {
                // A transparent box keeps the icon's own margins at any size.
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) },
                new GeometryDrawing { Pen = Stroke(StashIconShape.Stroke), Geometry = Geometry.Parse(StashIconShape.Tray) },
                new GeometryDrawing { Pen = Stroke(StashIconShape.ArrowStroke), Geometry = Geometry.Parse(StashIconShape.Arrow) },
            },
        };
        return new DrawingImage(group);
    }
}
