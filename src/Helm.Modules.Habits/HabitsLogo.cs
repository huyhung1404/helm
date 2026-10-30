using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.Habits;

/// <summary>Habits' icon as a WPF vector image (<see cref="HabitsIconShape"/>), used wherever the module appears.</summary>
public static class HabitsLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages, which may live on different windows.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = HabitsIconShape.Size;
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        for (var i = 0; i < HabitsIconShape.Colors.Length; i++)
        {
            var (x, y) = HabitsIconShape.Origin(i);
            var square = new RectangleGeometry(new Rect(x, y, HabitsIconShape.Cell, HabitsIconShape.Cell), HabitsIconShape.Radius, HabitsIconShape.Radius);
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Argb(HabitsIconShape.Colors[i])), null, square));
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
