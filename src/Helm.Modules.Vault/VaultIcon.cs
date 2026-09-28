using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.Vault;

/// <summary>The Vault icon as a WPF vector image (sharp at every size, the same in light and dark themes).</summary>
public static class VaultIcon
{
    /// <summary>Frozen, so every window and thread can share it.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        var gradient = new LinearGradientBrush(Argb(VaultIconShape.GradientStart), Argb(VaultIconShape.GradientEnd), new Point(0, 0), new Point(1, 1));
        var white = new SolidColorBrush(Argb(VaultIconShape.Foreground));
        var pen = new Pen(white, VaultIconShape.ShieldStroke) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, VaultIconShape.Size, VaultIconShape.Size))));
        group.Children.Add(new GeometryDrawing(gradient, null, Geometry.Parse(VaultIconShape.Background)));
        group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(VaultIconShape.Shield)));
        group.Children.Add(new GeometryDrawing(white, null, Geometry.Parse(VaultIconShape.Keyhole)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
