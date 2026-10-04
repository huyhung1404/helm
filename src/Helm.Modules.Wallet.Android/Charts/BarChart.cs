using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Helm.Modules.Wallet;

/// <summary>
/// A one-series column chart (<see cref="BarChartModel"/>), drawn like the Windows one: columns at most 24 px wide with
/// 4 px rounded tops, hairline gridlines at the top of the scale and half of it (labelled in the right gutter), an
/// optional reference line, sparing labels under the columns, and a readout line on top. A tap on a column shows its
/// readout and runs its command. The layout comes from <see cref="WalletCharts.Layout"/>.
/// </summary>
public sealed class BarChart : Control
{
    public static readonly StyledProperty<BarChartModel> ModelProperty =
        AvaloniaProperty.Register<BarChart, BarChartModel>(nameof(Model), BarChartModel.Empty);

    private const double CaptionBand = 28;
    private const double LabelBand = 20;
    private int _selected = -1;
    private ChartGeometry? _geometry;

    static BarChart()
    {
        AffectsRender<BarChart>(ModelProperty);
    }

    public BarChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public BarChartModel Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty) _selected = -1;
    }

    public override void Render(DrawingContext context)
    {
        var model = Model ?? BarChartModel.Empty;
        var width = Bounds.Width;
        var height = Bounds.Height;
        // Transparent, so the whole box takes the pointer.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (model.Bars.Count == 0 || width < 40 || height < 60) return;

        var dark = WalletTheme.IsDark(this);
        var primary = WalletTheme.Primary(this);
        var secondary = WalletTheme.Secondary(this);
        var maxLabel = WalletTheme.Text(this, model.MaxLabel, 11, secondary);
        var halfLabel = WalletTheme.Text(this, model.HalfLabel, 11, secondary);
        var gutter = Math.Max(maxLabel.Width, halfLabel.Width) + 6;
        var g = WalletCharts.Layout(model, width, height, CaptionBand, LabelBand, 0, gutter);
        _geometry = g;

        var grid = new Pen(WalletTheme.Brush(WalletPalette.Grid(dark)), 1);
        var baseline = new Pen(WalletTheme.Brush(WalletPalette.Baseline(dark)), 1);
        context.DrawLine(grid, new Point(g.PlotLeft, Snap(g.PlotTop)), new Point(g.PlotRight, Snap(g.PlotTop)));
        context.DrawLine(grid, new Point(g.PlotLeft, Snap(g.HalfY)), new Point(g.PlotRight, Snap(g.HalfY)));
        context.DrawLine(baseline, new Point(g.PlotLeft, Snap(g.BaselineY)), new Point(g.PlotRight, Snap(g.BaselineY)));
        context.DrawText(maxLabel, new Point(width - maxLabel.Width, g.PlotTop - maxLabel.Height / 2));
        context.DrawText(halfLabel, new Point(width - halfLabel.Width, g.HalfY - halfLabel.Height / 2));

        var accent = WalletTheme.Brush(WalletPalette.Accent(dark));
        var strong = WalletTheme.Brush(WalletPalette.AccentStrong(dark));
        var muted = WalletTheme.Brush(WalletPalette.Muted(dark));
        foreach (var bar in g.Bars)
        {
            var b = model.Bars[bar.Index];
            IBrush fill;
            if (model.IsGrouped)
            {
                // Two series keep their own colours; the context columns fade instead of turning grey.
                var argb = WalletPalette.Color(bar.Series == 0 ? model.Color : model.Color2, dark);
                fill = WalletTheme.Brush(b.IsEmphasis || bar.Index == _selected ? argb : WalletPalette.WithAlpha(argb, 0x59));
            }
            else
            {
                fill = bar.Index == _selected ? strong : b.IsEmphasis ? accent : muted;
            }
            context.DrawGeometry(fill, null, RoundedTop(bar.X, bar.Y, bar.Width, bar.Height));
        }

        // The reference level (the budget per day); the key under the chart names it.
        if (g.ReferenceY is { } y)
            context.DrawLine(new Pen(secondary, 1.5), new Point(g.PlotLeft, Snap(y)), new Point(g.PlotRight, Snap(y)));

        foreach (var (x, text) in g.Labels)
        {
            var label = WalletTheme.Text(this, text, 11, secondary);
            context.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, Math.Max(0, g.PlotRight - label.Width)), g.BaselineY + 4));
        }

        var index = _selected >= 0 ? _selected : model.DefaultIndex;
        if (index >= 0 && index < model.Bars.Count)
        {
            var caption = WalletTheme.Text(this, model.Bars[index].Caption, 13, primary, FontWeight.SemiBold);
            caption.MaxTextWidth = Math.Max(10, width);
            caption.MaxLineCount = 1;
            caption.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(caption, new Point(0, 2));
        }
    }

    private static double Snap(double y) => Math.Round(y) + 0.5;

    /// <summary>A column: 4 px rounded at the data end, square at the baseline.</summary>
    internal static Geometry RoundedTop(double x, double y, double w, double h)
    {
        var r = Math.Min(4, Math.Min(w / 2, h));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x, y + h), true);
            ctx.LineTo(new Point(x, y + r));
            ctx.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(x + w - r, y));
            ctx.ArcTo(new Point(x + w, y + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(x + w, y + h));
            ctx.EndFigure(true);
        }
        return geometry;
    }

    private int IndexAt(Point p)
    {
        var index = _geometry?.IndexAt(p.X) ?? -1;
        var model = Model;
        return index >= 0 && model is not null && index < model.Bars.Count && !model.Bars[index].IsEmpty ? index : -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var index = IndexAt(e.GetPosition(this));
        if (index < 0) return;
        _selected = index;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var index = IndexAt(e.GetPosition(this));
        if (index < 0 || index != _selected || Model is not { } model) return;
        if (model.Bars[index].Command is { } command && command.CanExecute(null)) command.Execute(null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        // A mouse (or a stylus hovering) shows the readout without a tap.
        if (e.Pointer.Type != PointerType.Mouse) return;
        var index = IndexAt(e.GetPosition(this));
        if (index == _selected) return;
        _selected = index;
        InvalidateVisual();
    }
}
