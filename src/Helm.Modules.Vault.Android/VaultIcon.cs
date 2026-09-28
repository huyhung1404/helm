using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.Vault;

/// <summary>The Vault icon as an Avalonia vector image, drawn from the same shapes as on Windows.</summary>
public static class VaultIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromUInt32(c);
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Argb(VaultIconShape.GradientStart), 0), new GradientStop(Argb(VaultIconShape.GradientEnd), 1) },
        };
        var white = new SolidColorBrush(Argb(VaultIconShape.Foreground));
        var pen = new Pen(white, VaultIconShape.ShieldStroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var group = new DrawingGroup
        {
            Children =
            {
                // A transparent box keeps the icon's own margins at any size.
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, VaultIconShape.Size, VaultIconShape.Size)) },
                new GeometryDrawing { Brush = gradient, Geometry = Geometry.Parse(VaultIconShape.Background) },
                new GeometryDrawing { Pen = pen, Geometry = Geometry.Parse(VaultIconShape.Shield) },
                new GeometryDrawing { Brush = white, Geometry = Geometry.Parse(VaultIconShape.Keyhole) },
            },
        };
        return new DrawingImage(group);
    }
}
