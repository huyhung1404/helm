using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Helm.Modules.Wallet;

/// <summary>
/// The month's spending by category as a donut (<see cref="DonutModel"/>), drawn like the Windows one: each slice in its
/// category's colour with a 2 px gap, the total in the middle. A tap on a slice keeps it bright, fades the others and
/// shows its name and share in the middle; a tap elsewhere goes back to the total. The legend under it (in XAML) names
/// every slice.
/// </summary>
public sealed class DonutChart : Control
{
    public static readonly StyledProperty<DonutModel> ModelProperty =
        AvaloniaProperty.Register<DonutChart, DonutModel>(nameof(Model), DonutModel.Empty);

    private int _selected = -1;

    static DonutChart()
    {
        AffectsRender<DonutChart>(ModelProperty);
    }

    public DonutChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public DonutModel Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty) _selected = -1;
    }

    private (Point Center, double Radius, double Thickness) Ring()
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var radius = size / 2 - 1;
        return (new Point(Bounds.Width / 2, Bounds.Height / 2), radius, Math.Max(10, radius * 0.3));
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var model = Model ?? DonutModel.Empty;
        if (model.IsEmpty || Bounds.Width < 40 || Bounds.Height < 40) return;
        var dark = WalletTheme.IsDark(this);
        var (center, radius, thickness) = Ring();
        var mid = radius - thickness / 2;
        var gap = model.Segments.Count > 1 ? 2 / mid * 180 / Math.PI : 0;
        var angle = -90.0;
        for (var i = 0; i < model.Segments.Count; i++)
        {
            var s = model.Segments[i];
            var sweep = s.Share * 360;
            var brush = WalletTheme.Category(s.Color, _selected >= 0 && _selected != i ? (byte)0x55 : (byte)0xFF, dark);
            var pen = new Pen(brush, thickness);
            if (sweep >= 359.9)
                context.DrawEllipse(null, pen, center, mid, mid);
            else if (sweep - gap > 0.2)
                context.DrawGeometry(null, pen, Arc(center, mid, angle + gap / 2, sweep - gap));
            angle += sweep;
        }

        string top, bottom;
        if (_selected >= 0 && _selected < model.Segments.Count)
        {
            top = model.Segments[_selected].ShareText;
            bottom = model.Segments[_selected].Name;
        }
        else
        {
            top = model.CenterValue;
            bottom = model.CenterLabel;
        }
        var inner = Math.Max(10, (radius - thickness) * 2 - 8);
        var big = WalletTheme.Text(this, top, 20, WalletTheme.Primary(this), FontWeight.SemiBold);
        big.MaxTextWidth = inner;
        big.MaxLineCount = 1;
        big.Trimming = TextTrimming.CharacterEllipsis;
        big.TextAlignment = TextAlignment.Center;
        var small = WalletTheme.Text(this, bottom, 12, WalletTheme.Secondary(this));
        small.MaxTextWidth = inner;
        small.MaxLineCount = 1;
        small.Trimming = TextTrimming.CharacterEllipsis;
        small.TextAlignment = TextAlignment.Center;
        var total = big.Height + small.Height;
        context.DrawText(big, new Point(center.X - inner / 2, center.Y - total / 2));
        context.DrawText(small, new Point(center.X - inner / 2, center.Y - total / 2 + big.Height));
    }

    private static Geometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        static Point On(Point c, double r, double degrees)
        {
            var rad = degrees * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(On(center, radius, startDegrees), false);
            ctx.ArcTo(On(center, radius, startDegrees + sweepDegrees), new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        return geometry;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var index = SegmentAt(e.GetPosition(this));
        _selected = index == _selected ? -1 : index;
        InvalidateVisual();
    }

    private int SegmentAt(Point p)
    {
        var model = Model;
        if (model is null || model.IsEmpty) return -1;
        var (center, radius, thickness) = Ring();
        var dx = p.X - center.X;
        var dy = p.Y - center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > radius + 8 || distance < radius - thickness - 8) return -1;
        var degrees = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
        var start = 0.0;
        for (var i = 0; i < model.Segments.Count; i++)
        {
            start += model.Segments[i].Share * 360;
            if (degrees < start) return i;
        }
        return model.Segments.Count - 1;
    }
}
