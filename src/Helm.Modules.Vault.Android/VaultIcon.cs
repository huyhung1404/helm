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
        // One gradient across the whole box, so the shield and the keyhole share it.
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(VaultIconShape.Size, VaultIconShape.Size, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Argb(VaultIconShape.GradientStart), 0), new GradientStop(Argb(VaultIconShape.GradientEnd), 1) },
        };
        var pen = new Pen(gradient, VaultIconShape.ShieldStroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var group = new DrawingGroup
        {
            Children =
            {
                // A transparent box keeps the icon's own margins at any size.
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, VaultIconShape.Size, VaultIconShape.Size)) },
                new GeometryDrawing { Pen = pen, Geometry = Geometry.Parse(VaultIconShape.Shield) },
                new GeometryDrawing { Brush = gradient, Geometry = Geometry.Parse(VaultIconShape.Keyhole) },
            },
        };
        return new DrawingImage(group);
    }
}
