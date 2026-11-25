using Avalonia;
using Avalonia.Media;

namespace Helm.General;

/// <summary>
/// The icons of General's parts, drawn like every tool icon in Helm: a frameless vector in a 100 × 100 box that it fills
/// edge to edge, strokes of 10 with round ends, one gradient across the box from bottom left to top right, each part
/// its own colours. So the list reads like the list of tools, at the same size and weight.
/// </summary>
public static class GeneralIcons
{
    private const double Size = 100;
    private const double Stroke = 10;

    public static IImage? For(string part) => part switch
    {
        "tools" => Tools.Value,
        "sync" => Sync.Value,
        "updates" => Updates.Value,
        "appearance" => Appearance.Value,
        "background" => Background.Value,
        "help" => Help.Value,
        _ => null,
    };

    /// <summary>Four rounded squares, the last one filled: the tools, some on.</summary>
    private static readonly Lazy<IImage> Tools = new(() => Draw(0xFF7B5CFA, 0xFFE25CFF, (pen, fill, g) =>
    {
        g.Add(pen, new RectangleGeometry(new Rect(6, 6, 38, 38), 10, 10));
        g.Add(pen, new RectangleGeometry(new Rect(56, 6, 38, 38), 10, 10));
        g.Add(pen, new RectangleGeometry(new Rect(6, 56, 38, 38), 10, 10));
        g.Fill(fill, new RectangleGeometry(new Rect(51, 51, 48, 48), 13, 13));
    }));

    /// <summary>Two arrows chasing each other round: sync, in Helm's own blue.</summary>
    private static readonly Lazy<IImage> Sync = new(() => Draw(0xFF0077F2, 0xFF00C6E0, (pen, _, g) =>
    {
        g.Add(pen, Geometry.Parse("M10 44A40 40 0 0 1 84 28"));
        g.Add(pen, Geometry.Parse("M88 8V30H66"));
        g.Add(pen, Geometry.Parse("M90 56A40 40 0 0 1 16 72"));
        g.Add(pen, Geometry.Parse("M12 92V70H34"));
    }));

    /// <summary>An arrow down into a ring: an update to take.</summary>
    private static readonly Lazy<IImage> Updates = new(() => Draw(0xFF16A34A, 0xFF5EEAD4, (pen, _, g) =>
    {
        g.Add(pen, new EllipseGeometry(new Rect(6, 6, 88, 88)));
        g.Add(pen, Geometry.Parse("M50 27V68"));
        g.Add(pen, Geometry.Parse("M33 52L50 69L67 52"));
    }));

    /// <summary>A disc half light, half dark: the theme (and how Helm starts).</summary>
    private static readonly Lazy<IImage> Appearance = new(() => Draw(0xFFF59E0B, 0xFFFB7185, (pen, fill, g) =>
    {
        g.Add(pen, new EllipseGeometry(new Rect(6, 6, 88, 88)));
        g.Fill(fill, Geometry.Parse("M50 6A44 44 0 0 0 50 94Z"));
    }));

    /// <summary>A pulse line: something keeps running.</summary>
    private static readonly Lazy<IImage> Background = new(() => Draw(0xFF00CFC0, 0xFF34D399, (pen, _, g) =>
    {
        g.Add(pen, Geometry.Parse("M5 55H25L37 20L55 82L67 44L75 55H95"));
    }));

    /// <summary>A question mark in a ring: help.</summary>
    private static readonly Lazy<IImage> Help = new(() => Draw(0xFF06AEFF, 0xFF6366F1, (pen, fill, g) =>
    {
        g.Add(pen, new EllipseGeometry(new Rect(6, 6, 88, 88)));
        g.Add(pen, Geometry.Parse("M36 39A14 14 0 1 1 57 51C52 54 50 57 50 62"));
        g.Fill(fill, new EllipseGeometry(new Rect(44, 70, 12, 12)));
    }));

    private sealed class Drawing(DrawingGroup group)
    {
        public void Add(Pen pen, Geometry geometry) => group.Children.Add(new GeometryDrawing { Pen = pen, Geometry = geometry });

        public void Fill(IBrush brush, Geometry geometry) => group.Children.Add(new GeometryDrawing { Brush = brush, Geometry = geometry });
    }

    private static IImage Draw(uint start, uint end, Action<Pen, IBrush, Drawing> draw)
    {
        // One gradient across the whole box, bottom left to top right, like the tool icons.
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, Size, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(Size, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Color.FromUInt32(start), 0), new GradientStop(Color.FromUInt32(end), 1) },
        };
        var pen = new Pen(gradient, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        // A transparent box keeps the icon's own margins at any size.
        var group = new DrawingGroup { Children = { new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, Size, Size)) } } };
        draw(pen, gradient, new Drawing(group));
        return new DrawingImage(group);
    }
}
