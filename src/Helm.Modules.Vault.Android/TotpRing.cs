using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Helm.Modules.Vault;

/// <summary>How long a two-factor code stays valid, as a small ring that empties (next to the code in the list).</summary>
public sealed class TotpRing : Control
{
    /// <summary>0 to 100, like <see cref="ViewModels.VaultCardViewModel.TotpProgress"/>.</summary>
    public static readonly StyledProperty<double> PercentProperty = AvaloniaProperty.Register<TotpRing, double>(nameof(Percent));

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<TotpRing, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<TotpRing, IBrush?>(nameof(Track));

    static TotpRing() => AffectsRender<TotpRing>(PercentProperty, StrokeProperty, TrackProperty);

    public double Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - 1.5;
        if (Track is { } track) context.DrawEllipse(null, new Pen(track, 2.5), center, radius, radius);
        var f = Math.Clamp(Percent / 100, 0, 1);
        if (f <= 0 || Stroke is not { } stroke) return;
        var pen = new Pen(stroke, 2.5, lineCap: PenLineCap.Round);
        if (f >= 0.999)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var angle = f * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}
