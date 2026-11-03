using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Helm.Modules.Vocab;

/// <summary>The design's line icons (24 × 24, stroked 1.7 with round ends), point for point.</summary>
public enum LineIconKind
{
    Plus,
    X,
    Left,
    Right,
    Down,
    Up,
    Back,
    Menu,
    Search,
    Sort,
    Edit,
    Trash,
    Dots,
    Speaker,
    Video,
    Cap,
    Book,
    Branch,
    Sparkle,
    Hourglass,
    Check,
    CheckCircle,
    XCircle,
    Star,
    EyeOff,
    Eye,
    Redo,
    Skip,
    Open,
    Pc,
    Warn,
    Copy,
    Play,
    Document,
}

/// <summary>
/// One of the design's line icons (<c>&lt;local:LineIcon Kind="Speaker" Size="18" /&gt;</c>), in the inherited text colour
/// unless <see cref="Foreground"/> is set. The stroke scales with the icon, as an SVG's does with its viewBox.
/// </summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<LineIconKind> KindProperty = AvaloniaProperty.Register<LineIcon, LineIconKind>(nameof(Kind));

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<LineIcon, double>(nameof(Size), 20);

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<LineIcon>();

    static LineIcon()
    {
        AffectsRender<LineIcon>(KindProperty, ForegroundProperty, SizeProperty);
        AffectsMeasure<LineIcon>(SizeProperty);
    }

    public LineIconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Its width and height (the canvas's <c>width: 22px; height: 22px</c>).</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Foreground is not { } brush) return;
        var shape = LineIconShapes.Get(Kind);
        var scale = Size / 24;
        var offset = new Point((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            if (shape.Stroke is { } stroke)
                context.DrawGeometry(null, new Pen(brush, 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), stroke);
            if (shape.Fill is { } fill) context.DrawGeometry(brush, null, fill);
        }
    }
}

/// <summary>The icons' shapes, copied from the design's SVG symbols (circles and rounded boxes written as paths).</summary>
internal static class LineIconShapes
{
    private static readonly Dictionary<LineIconKind, (Geometry? Stroke, Geometry? Fill)> Cache = [];

    public static (Geometry? Stroke, Geometry? Fill) Get(LineIconKind kind)
    {
        if (Cache.TryGetValue(kind, out var shape)) return shape;
        var (stroke, fill) = Data(kind);
        shape = (stroke is null ? null : Geometry.Parse(stroke), fill is null ? null : Geometry.Parse(fill));
        Cache[kind] = shape;
        return shape;
    }

    private static (string? Stroke, string? Fill) Data(LineIconKind kind) => kind switch
    {
        LineIconKind.Plus => ("M12,5 V19 M5,12 H19", null),
        LineIconKind.X => ("M6,6 L18,18 M18,6 L6,18", null),
        LineIconKind.Left => ("M15,18 L9,12 L15,6", null),
        LineIconKind.Right => ("M9,18 L15,12 L9,6", null),
        LineIconKind.Down => ("M6,9 L12,15 L18,9", null),
        // The section's chevron turned half a turn when it is open.
        LineIconKind.Up => ("M18,15 L12,9 L6,15", null),
        LineIconKind.Back => ("M19,12 H5 M11,6 L5,12 L11,18", null),
        LineIconKind.Menu => ("M4,7 H20 M4,12 H20 M4,17 H20", null),
        LineIconKind.Search => (Circle(11, 11, 6.5) + " M20,20 L15.8,15.8", null),
        LineIconKind.Sort => ("M7,4 V20 M3.5,16.5 L7,20 L10.5,16.5 M13,6 H21 M13,11 H19 M13,16 H17", null),
        LineIconKind.Edit => ("M4,20 H8 L19,9 L15,5 L4,16 Z M13.5,6.5 L17.5,10.5", null),
        LineIconKind.Trash => ("M4,7 H20 M10,11 V17 M14,11 V17 M6,7 L7,20 H17 L18,7 M9,7 V4 H15 V7", null),
        LineIconKind.Dots => (null, Circle(12, 6, 1.5) + " " + Circle(12, 12, 1.5) + " " + Circle(12, 18, 1.5)),
        LineIconKind.Speaker => ("M4,9.5 H7.5 L12,5.5 V18.5 L7.5,14.5 H4 Z M15.5,9 A4,4 0 0 1 15.5,15 M18,6.5 A7.5,7.5 0 0 1 18,17.5", null),
        LineIconKind.Video => (Box(3.5, 5.5, 17, 13, 2.5) + " M10.5,9.5 V14.5 L14.7,12 Z", null),
        LineIconKind.Cap => ("M2.5,9.5 L12,5 L21.5,9.5 L12,14 Z M6.5,11.5 V16 C9.5,18.4 14.5,18.4 17.5,16 V11.5 M21.5,9.5 V14.5", null),
        LineIconKind.Book => ("M5,4.5 H16.5 A2,2 0 0 1 18.5,6.5 V20 H7 A2,2 0 0 1 5,18 Z M5,18 A2,2 0 0 1 7,16 H18.5 M9,8.5 H15", null),
        LineIconKind.Branch => (Box(9, 3.5, 6, 5, 1.2) + " " + Box(3, 15.5, 6, 5, 1.2) + " " + Box(15, 15.5, 6, 5, 1.2) + " M12,8.5 V12 M6,15.5 V12 H18 V15.5", null),
        LineIconKind.Sparkle => ("M12,3.5 L13.9,8.6 L19,10.5 L13.9,12.4 L12,17.5 L10.1,12.4 L5,10.5 L10.1,8.6 Z M18.5,16 V20 M16.5,18 H20.5", null),
        LineIconKind.Hourglass => ("M7,3.5 H17 M7,20.5 H17 M8,3.5 C8,8 16,8 16,12 S8,16 8,20.5 M16,3.5 C16,8 8,8 8,12 S16,16 16,20.5", null),
        LineIconKind.Check => ("M5,12.5 L9.5,17 L19,7.5", null),
        LineIconKind.CheckCircle => (Circle(12, 12, 8.5) + " M8,12.3 L10.8,15 L16,9.5", null),
        LineIconKind.XCircle => (Circle(12, 12, 8.5) + " M9,9 L15,15 M15,9 L9,15", null),
        LineIconKind.Star => ("M12,3.5 L14.6,8.8 L20.5,9.7 L16.2,13.8 L17.2,19.6 L12,16.9 L6.8,19.6 L7.8,13.8 L3.5,9.7 L9.4,8.8 Z", null),
        LineIconKind.EyeOff => ("M3,3 L21,21 M10.6,6.1 A9.6,9.6 0 0 1 12,6 C17,6 20.5,10.5 21.5,12 C21,12.8 19.9,14.3 18.3,15.6 "
            + "M6.4,7.6 C4.6,8.8 3.3,10.6 2.5,12 C3.5,13.5 7,18 12,18 C13.6,18 15,17.6 16.3,16.9 M9.9,9.9 A3,3 0 0 0 14.1,14.1", null),
        LineIconKind.Eye => ("M2.5,12 C3.5,10.5 7,6 12,6 C17,6 20.5,10.5 21.5,12 C20.5,13.5 17,18 12,18 C7,18 3.5,13.5 2.5,12 Z " + Circle(12, 12, 3), null),
        LineIconKind.Redo => ("M19.5,12 A7.5,7.5 0 1 1 17.3,6.7 M19.5,4.5 V8.5 H15.5", null),
        LineIconKind.Skip => ("M5,6 L13,12 L5,18 Z M17,6 V18", null),
        LineIconKind.Open => ("M14,4.5 H19.5 V10 M19.5,4.5 L11,13 M17,14 V19.5 H4.5 V7 H10", null),
        LineIconKind.Pc => (Box(3, 4.5, 18, 12, 1.5) + " M9,20 H15 M12,16.5 V20", null),
        LineIconKind.Warn => ("M12,4 L21,19.5 H3 Z M12,10 V14.5 M12,17.2 V17.3", null),
        LineIconKind.Copy => (Box(8.5, 8.5, 11.5, 11.5, 2) + " M15.5,8.5 V5.5 A1.5,1.5 0 0 0 14,4 H5.5 A1.5,1.5 0 0 0 4,5.5 V14 A1.5,1.5 0 0 0 5.5,15.5 H8.5", null),
        LineIconKind.Play => ("M7,5 L19,12 L7,19 Z", null),
        // Not in the design (Add text): a page with a folded corner and two lines, drawn the same way.
        LineIconKind.Document => ("M6.5,3.5 H14 L18.5,8 V20.5 H6.5 Z M14,3.5 V8 H18.5 M9.5,12.5 H15.5 M9.5,16 H15.5", null),
        _ => (null, null),
    };

    private static string Circle(double cx, double cy, double r) =>
        F($"M{cx - r},{cy} A{r},{r} 0 1 0 {cx + r},{cy} A{r},{r} 0 1 0 {cx - r},{cy} Z");

    private static string Box(double x, double y, double w, double h, double r) =>
        F($"M{x + r},{y} H{x + w - r} A{r},{r} 0 0 1 {x + w},{y + r} V{y + h - r} A{r},{r} 0 0 1 {x + w - r},{y + h} H{x + r} ")
        + F($"A{r},{r} 0 0 1 {x},{y + h - r} V{y + r} A{r},{r} 0 0 1 {x + r},{y} Z");

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
