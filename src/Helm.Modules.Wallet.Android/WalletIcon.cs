using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.Wallet;

/// <summary>The Wallet icon as an Avalonia vector image, drawn from the same shapes as on Windows.</summary>
public static class WalletIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        const double size = WalletIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right.
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Color.FromUInt32(WalletIconShape.GradientStart), 0),
                new GradientStop(Color.FromUInt32(WalletIconShape.GradientEnd), 1),
            },
        };
        var group = new DrawingGroup
        {
            Children =
            {
                // A transparent box keeps the icon's own margins at any size.
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) },
                new GeometryDrawing { Brush = gradient, Geometry = Geometry.Parse(WalletIconShape.Card) },
                new GeometryDrawing
                {
                    Pen = new Pen(gradient, WalletIconShape.Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                    Geometry = Geometry.Parse(WalletIconShape.Body),
                },
                new GeometryDrawing { Brush = gradient, Geometry = Geometry.Parse(WalletIconShape.Clasp) },
            },
        };
        return new DrawingImage(group);
    }
}
