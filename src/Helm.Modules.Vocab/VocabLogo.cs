using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.Vocab;

/// <summary>The Vocabulary icon as a WPF vector image (<see cref="VocabIconShape"/>), used wherever the module appears.</summary>
public static class VocabLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages, which may live on different windows.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = VocabIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right, shared by the dots and the branches.
        var gradient = new LinearGradientBrush(Argb(VocabIconShape.GradientStart), Argb(VocabIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        var pen = new Pen(gradient, VocabIconShape.Stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(VocabIconShape.Branches)));
        group.Children.Add(new GeometryDrawing(gradient, null, Geometry.Parse(VocabIconShape.Dots)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
