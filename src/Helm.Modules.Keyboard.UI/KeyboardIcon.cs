using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.Keyboard.Ui;

/// <summary>The Helm Keyboard icon as an Avalonia vector image (<see cref="KeyboardIconShape"/>).</summary>
public static class KeyboardIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        const double size = KeyboardIconShape.Size;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromUInt32(KeyboardIconShape.GradientStart), 0), new GradientStop(Color.FromUInt32(KeyboardIconShape.GradientEnd), 1) },
        };
        // A transparent box keeps the icon's own margins at any size.
        var group = new DrawingGroup
        {
            Children = { new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) } },
        };
        foreach (var (x, y, width, height) in KeyboardIconShape.Keys)
            group.Children.Add(new GeometryDrawing { Brush = gradient, Geometry = new RectangleGeometry(new Rect(x, y, width, height), KeyboardIconShape.Radius, KeyboardIconShape.Radius) });
        return new DrawingImage(group);
    }
}
