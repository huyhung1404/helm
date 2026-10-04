using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Helm.Modules.Wallet;

/// <summary>
/// The month's spending by category as a donut (<see cref="DonutModel"/>): each slice in its category's colour with a
/// 2 px gap between slices, the total in the middle. The slice under the pointer stays bright, the others fade, and the
/// middle shows its name, share and amount. The legend beside it (in XAML) names every slice, so colour is never the
/// only way to tell them apart.
/// </summary>
public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(DonutModel), typeof(DonutChart),
        new FrameworkPropertyMetadata(DonutModel.Empty, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((DonutChart)d)._hover = -1));

    private int _hover = -1;

    public DonutModel Model
    {
        get => (DonutModel)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private (Point Center, double Radius, double Thickness) Ring()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var radius = size / 2 - 1;
        return (new Point(ActualWidth / 2, ActualHeight / 2), radius, Math.Max(10, radius * 0.3));
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var model = Model ?? DonutModel.Empty;
        if (model.IsEmpty || ActualWidth < 40 || ActualHeight < 40) return;
        var dark = WalletTheme.IsDark(this);
        var (center, radius, thickness) = Ring();
        var mid = radius - thickness / 2;
        // A 2 px gap between slices, measured on the ring's middle.
        var gap = model.Segments.Count > 1 ? 2 / mid * 180 / Math.PI : 0;
        var angle = -90.0;
        for (var i = 0; i < model.Segments.Count; i++)
        {
            var s = model.Segments[i];
            var sweep = s.Share * 360;
            var argb = s.Color < 0 ? WalletPalette.Other(dark) : WalletPalette.Color(s.Color, dark);
            if (_hover >= 0 && _hover != i) argb = WalletPalette.WithAlpha(argb, 0x55);
            var pen = new Pen(WalletTheme.Frozen(argb), thickness);
            if (sweep >= 359.9)
                dc.DrawEllipse(null, pen, center, mid, mid);
            else if (sweep - gap > 0.2)
                dc.DrawGeometry(null, pen, Arc(center, mid, angle + gap / 2, sweep - gap));
            angle += sweep;
        }

        var primary = WalletTheme.Text(this, "TextFillColorPrimaryBrush", Brushes.Black);
        var secondary = WalletTheme.Text(this, "TextFillColorSecondaryBrush", Brushes.Gray);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var font = System.Windows.Documents.TextElement.GetFontFamily(this) ?? SystemFonts.MessageFontFamily;
        string top, bottom;
        if (_hover >= 0 && _hover < model.Segments.Count)
        {
            var s = model.Segments[_hover];
            top = s.ShareText;
            bottom = s.Name;
        }
        else
        {
            top = model.CenterValue;
            bottom = model.CenterLabel;
        }
        var inner = (radius - thickness) * 2 - 8;
        var big = new FormattedText(top, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 20, primary, dpi)
            { MaxTextWidth = Math.Max(10, inner), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
        var small = new FormattedText(bottom, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(font.Source), 12, secondary, dpi)
            { MaxTextWidth = Math.Max(10, inner), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
        var total = big.Height + small.Height;
        dc.DrawText(big, new Point(center.X - big.MaxTextWidth / 2, center.Y - total / 2));
        dc.DrawText(small, new Point(center.X - small.MaxTextWidth / 2, center.Y - total / 2 + big.Height));
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
            ctx.BeginFigure(On(center, radius, startDegrees), false, false);
            ctx.ArcTo(On(center, radius, startDegrees + sweepDegrees), new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = SegmentAt(e.GetPosition(this));
        if (index == _hover) return;
        _hover = index;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
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
        if (distance > radius + 2 || distance < radius - thickness - 2) return -1;
        // Clockwise from 12 o'clock, 0–360.
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
