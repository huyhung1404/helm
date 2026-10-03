using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.Missions;

/// <summary>The Missions icon as an Avalonia vector image, drawn from the same shapes as on Windows.</summary>
public static class MissionsIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        const double size = MissionsIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right.
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Color.FromUInt32(MissionsIconShape.GradientStart), 0),
                new GradientStop(Color.FromUInt32(MissionsIconShape.GradientEnd), 1),
            },
        };
        Pen Stroke(double width) => new(gradient, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var group = new DrawingGroup
        {
            Children =
            {
                // A transparent box keeps the icon's own margins at any size.
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) },
                new GeometryDrawing { Pen = Stroke(MissionsIconShape.Stroke), Geometry = Geometry.Parse(MissionsIconShape.Path) },
                new GeometryDrawing { Pen = Stroke(MissionsIconShape.Stroke), Geometry = Geometry.Parse(MissionsIconShape.Pole) },
                new GeometryDrawing { Brush = gradient, Pen = Stroke(MissionsIconShape.FlagStroke), Geometry = Geometry.Parse(MissionsIconShape.Flag) },
                new GeometryDrawing { Brush = gradient, Geometry = Geometry.Parse(MissionsIconShape.Start) },
            },
        };
        return new DrawingImage(group);
    }
}
