using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.QuickCapture;

/// <summary>The Quick Capture icon as an Avalonia vector image, drawn from the same shape as on Windows.</summary>
public static class QuickCaptureIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        const double size = QuickCaptureIconShape.Size;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromUInt32(QuickCaptureIconShape.GradientStart), 0), new GradientStop(Color.FromUInt32(QuickCaptureIconShape.GradientEnd), 1) },
        };
        var group = new DrawingGroup
        {
            Children =
            {
                // A transparent box keeps the icon's own margins at any size.
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) },
                new GeometryDrawing
                {
                    Brush = gradient,
                    Pen = new Pen(gradient, QuickCaptureIconShape.Stroke, lineJoin: PenLineJoin.Round),
                    Geometry = Geometry.Parse(QuickCaptureIconShape.Bolt),
                },
            },
        };
        return new DrawingImage(group);
    }
}
