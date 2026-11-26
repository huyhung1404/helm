using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.WindowZones.Ui;

/// <summary>The Window Zones icon as an Avalonia vector image (<see cref="WindowZonesIconShape"/>).</summary>
public static class WindowZonesIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        const double size = WindowZonesIconShape.Size;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromUInt32(WindowZonesIconShape.GradientStart), 0), new GradientStop(Color.FromUInt32(WindowZonesIconShape.GradientEnd), 1) },
        };
        var group = new DrawingGroup
        {
            Children =
            {
                // A transparent box keeps the icon's own margins at any size.
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) },
                new GeometryDrawing { Pen = new Pen(gradient, WindowZonesIconShape.Stroke, lineJoin: PenLineJoin.Round), Geometry = Geometry.Parse(WindowZonesIconShape.Zones) },
            },
        };
        return new DrawingImage(group);
    }
}
