using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.Wallet;

/// <summary>Wallet's icon as a WPF vector image (<see cref="WalletIconShape"/>), used wherever the module appears.</summary>
public static class WalletLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages, which may live on different windows.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = WalletIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right.
        var gradient = new LinearGradientBrush(Argb(WalletIconShape.GradientStart), Argb(WalletIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        var stroke = new Pen(gradient, WalletIconShape.Stroke) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(new GeometryDrawing(gradient, null, Geometry.Parse(WalletIconShape.Card)));
        group.Children.Add(new GeometryDrawing(null, stroke, Geometry.Parse(WalletIconShape.Body)));
        group.Children.Add(new GeometryDrawing(gradient, null, Geometry.Parse(WalletIconShape.Clasp)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
