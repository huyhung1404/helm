using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.Ssh;

/// <summary>The SSH icon as an Avalonia vector image, drawn from the same shapes as on Windows.</summary>
public static class SshIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        const double size = SshIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right.
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromUInt32(SshIconShape.GradientStart), 0), new GradientStop(Color.FromUInt32(SshIconShape.GradientEnd), 1) },
        };
        var pen = new Pen(gradient, SshIconShape.Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var group = new DrawingGroup
        {
            Children =
            {
                // A transparent box keeps the icon's own margins at any size.
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) },
                new GeometryDrawing { Pen = pen, Geometry = Geometry.Parse(SshIconShape.Window) },
                new GeometryDrawing { Pen = pen, Geometry = Geometry.Parse(SshIconShape.Chevron) },
                new GeometryDrawing { Pen = pen, Geometry = Geometry.Parse(SshIconShape.Cursor) },
            },
        };
        return new DrawingImage(group);
    }
}
