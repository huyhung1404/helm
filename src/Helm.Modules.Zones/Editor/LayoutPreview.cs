using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Helm.Modules.Zones.Layouts;

namespace Helm.Modules.Zones.Editor;

/// <summary>Draws a set of fractional zones scaled into the control (layout picker thumbnails and the live preview).</summary>
public sealed class LayoutPreview : FrameworkElement
{
    public static readonly DependencyProperty ZonesProperty = DependencyProperty.Register(
        nameof(Zones), typeof(IReadOnlyList<ZoneRect>), typeof(LayoutPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(double), typeof(LayoutPreview),
        new FrameworkPropertyMetadata(16.0 / 9, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(LayoutPreview), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowNumbersProperty = DependencyProperty.Register(
        nameof(ShowNumbers), typeof(bool), typeof(LayoutPreview), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ZoneFillProperty = DependencyProperty.Register(
        nameof(ZoneFill), typeof(Brush), typeof(LayoutPreview),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ZoneStrokeProperty = DependencyProperty.Register(
        nameof(ZoneStroke), typeof(Brush), typeof(LayoutPreview),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>When true the control fills its arranged size instead of keeping <see cref="AspectRatio"/>.</summary>
    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch), typeof(bool), typeof(LayoutPreview), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public IReadOnlyList<ZoneRect>? Zones
    {
        get => (IReadOnlyList<ZoneRect>?)GetValue(ZonesProperty);
        set => SetValue(ZonesProperty, value);
    }

    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public bool ShowNumbers
    {
        get => (bool)GetValue(ShowNumbersProperty);
        set => SetValue(ShowNumbersProperty, value);
    }

    public Brush ZoneFill
    {
        get => (Brush)GetValue(ZoneFillProperty);
        set => SetValue(ZoneFillProperty, value);
    }

    public Brush ZoneStroke
    {
        get => (Brush)GetValue(ZoneStrokeProperty);
        set => SetValue(ZoneStrokeProperty, value);
    }

    public bool Stretch
    {
        get => (bool)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Stretch) return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? 0 : available.Height);
        var ratio = AspectRatio <= 0 ? 16.0 / 9 : AspectRatio;
        var width = double.IsInfinity(available.Width) ? 160 : available.Width;
        var height = width / ratio;
        if (!double.IsInfinity(available.Height) && height > available.Height)
        {
            height = available.Height;
            width = height * ratio;
        }
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0 || Zones is not { } zones) return;

        var pen = new Pen(ZoneStroke, 1);
        var half = Gap / 2;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            var rect = new Rect(z.X * w + half, z.Y * h + half, Math.Max(1, z.Width * w - Gap), Math.Max(1, z.Height * h - Gap));
            dc.DrawRoundedRectangle(ZoneFill, pen, rect, 3, 3);
            if (!ShowNumbers) continue;
            var text = new FormattedText((i + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), Math.Clamp(Math.Min(rect.Width, rect.Height) / 3, 10, 64), ZoneStroke, dpi);
            dc.DrawText(text, new Point(rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2));
        }
    }
}
