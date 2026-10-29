using Avalonia;
using Avalonia.Media;

namespace Helm.Modules.Vocab;

/// <summary>The Vocabulary icon as an Avalonia vector image, drawn from the same shapes as on Windows.</summary>
public static class VocabIcon
{
    public static IImage Image { get; } = Create();

    private static DrawingImage Create()
    {
        const double size = VocabIconShape.Size;
        // One gradient across the whole box, bottom-left to top-right, shared by the dots and the branches.
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(size, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromUInt32(VocabIconShape.GradientStart), 0), new GradientStop(Color.FromUInt32(VocabIconShape.GradientEnd), 1) },
        };
        var group = new DrawingGroup
        {
            Children =
            {
                // A transparent box keeps the icon's own margins at any size.
                new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, size, size)) },
                new GeometryDrawing { Pen = new Pen(gradient, VocabIconShape.Stroke, lineCap: PenLineCap.Round), Geometry = Geometry.Parse(VocabIconShape.Branches) },
                new GeometryDrawing { Brush = gradient, Geometry = Geometry.Parse(VocabIconShape.Dots) },
            },
        };
        return new DrawingImage(group);
    }
}
