using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.Workspace;

/// <summary>The Workspace Controller icon as an Avalonia vector image, drawn from the same shapes as on Windows.</summary>
public static class WorkspaceIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        const double size = WorkspaceIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right.
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromUInt32(WorkspaceIconShape.GradientStart), 0), new GradientStop(Color.FromUInt32(WorkspaceIconShape.GradientEnd), 1) },
        };
        var group = new DrawingGroup
        {
            Children =
            {
                // A transparent box keeps the icon's own margins at any size.
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) },
                new GeometryDrawing { Pen = new Pen(gradient, WorkspaceIconShape.Stroke, lineJoin: PenLineJoin.Round), Geometry = Geometry.Parse(WorkspaceIconShape.Phone) },
                new GeometryDrawing { Brush = gradient, Geometry = Geometry.Parse(WorkspaceIconShape.HomeBar) },
                new GeometryDrawing { Pen = new Pen(gradient, WorkspaceIconShape.Stroke, lineCap: PenLineCap.Round), Geometry = Geometry.Parse(WorkspaceIconShape.WaveNear) },
                new GeometryDrawing { Pen = new Pen(gradient, WorkspaceIconShape.Stroke, lineCap: PenLineCap.Round), Geometry = Geometry.Parse(WorkspaceIconShape.WaveFar) },
            },
        };
        return new DrawingImage(group);
    }
}
