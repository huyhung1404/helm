using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.Habits;

/// <summary>The Habits icon as an Avalonia vector image, drawn from the same shapes as on Windows.</summary>
public static class HabitsIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        const double size = HabitsIconShape.Size;
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) });
        for (var i = 0; i < HabitsIconShape.Colors.Length; i++)
        {
            var (x, y) = HabitsIconShape.Origin(i);
            group.Children.Add(new GeometryDrawing
            {
                Brush = new SolidColorBrush(Color.FromUInt32(HabitsIconShape.Colors[i])),
                Geometry = new RectangleGeometry(new Rect(x, y, HabitsIconShape.Cell, HabitsIconShape.Cell), HabitsIconShape.Radius, HabitsIconShape.Radius),
            });
        }
        return new DrawingImage(group);
    }
}
