using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Helm.Modules.Wallet;

/// <summary>
/// A one-series column chart (<see cref="BarChartModel"/>): columns at most 24 px wide with 4 px rounded tops, hairline
/// gridlines at the top of the scale and half of it (labelled in the right gutter), an optional reference line, sparing
/// labels under the columns, and a readout line on top for the column under the pointer (or the default one). A click
/// on a column runs its command. The layout comes from <see cref="WalletCharts.Layout"/>, shared with Android.
/// </summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(BarChartModel), typeof(BarChart),
        new FrameworkPropertyMetadata(BarChartModel.Empty, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((BarChart)d)._hover = -1));

    private const double CaptionBand = 28;
    private const double LabelBand = 20;
    private int _hover = -1;
    private ChartGeometry? _geometry;

    public BarChartModel Model
    {
        get => (BarChartModel)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var model = Model ?? BarChartModel.Empty;
        var width = ActualWidth;
        var height = ActualHeight;
        // Transparent, so the whole box takes the pointer.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (model.Bars.Count == 0 || width < 40 || height < 60) return;

        var dark = WalletTheme.IsDark(this);
        var primary = WalletTheme.Text(this, "TextFillColorPrimaryBrush", Brushes.Black);
        var secondary = WalletTheme.Text(this, "TextFillColorSecondaryBrush", Brushes.Gray);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TextElementFont(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(TextElementFont(), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        FormattedText Text(string s, double size, Brush brush, Typeface face) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, dpi);

        var maxLabel = Text(model.MaxLabel, 11, secondary, typeface);
        var halfLabel = Text(model.HalfLabel, 11, secondary, typeface);
        var gutter = Math.Max(maxLabel.Width, halfLabel.Width) + 8;
        var g = WalletCharts.Layout(model, width, height, CaptionBand, LabelBand, 0, gutter);
        _geometry = g;

        // Gridlines: hairlines, recessive; the baseline a step stronger.
        var grid = new Pen(WalletTheme.Frozen(WalletPalette.Grid(dark)), 1);
        var baseline = new Pen(WalletTheme.Frozen(WalletPalette.Baseline(dark)), 1);
        dc.DrawLine(grid, new Point(g.PlotLeft, Snap(g.PlotTop)), new Point(g.PlotRight, Snap(g.PlotTop)));
        dc.DrawLine(grid, new Point(g.PlotLeft, Snap(g.HalfY)), new Point(g.PlotRight, Snap(g.HalfY)));
        dc.DrawLine(baseline, new Point(g.PlotLeft, Snap(g.BaselineY)), new Point(g.PlotRight, Snap(g.BaselineY)));
        dc.DrawText(maxLabel, new Point(width - maxLabel.Width, g.PlotTop - maxLabel.Height / 2));
        dc.DrawText(halfLabel, new Point(width - halfLabel.Width, g.HalfY - halfLabel.Height / 2));

        var accent = WalletTheme.Frozen(WalletPalette.Accent(dark));
        var strong = WalletTheme.Frozen(WalletPalette.AccentStrong(dark));
        var muted = WalletTheme.Frozen(WalletPalette.Muted(dark));
        foreach (var bar in g.Bars)
        {
            var b = model.Bars[bar.Index];
            Brush fill;
            if (model.IsGrouped)
            {
                // Two series keep their own colours; the context columns fade instead of turning grey.
                var argb = WalletPalette.Color(bar.Series == 0 ? model.Color : model.Color2, dark);
                fill = WalletTheme.Frozen(b.IsEmphasis || bar.Index == _hover ? argb : WalletPalette.WithAlpha(argb, 0x59));
            }
            else
            {
                fill = bar.Index == _hover ? strong : b.IsEmphasis ? accent : muted;
            }
            dc.DrawGeometry(fill, null, RoundedTop(bar.X, bar.Y, bar.Width, bar.Height));
        }

        // The reference level (the budget per day); the key under the chart names it, so no label crosses the columns.
        if (g.ReferenceY is { } y)
            dc.DrawLine(new Pen(secondary, 1.5), new Point(g.PlotLeft, Snap(y)), new Point(g.PlotRight, Snap(y)));

        foreach (var (x, text) in g.Labels)
        {
            var label = Text(text, 11, secondary, typeface);
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, Math.Max(0, g.PlotRight - label.Width)), g.BaselineY + 4));
        }

        // The readout: the column under the pointer, else the default one.
        var index = _hover >= 0 ? _hover : model.DefaultIndex;
        if (index >= 0 && index < model.Bars.Count)
        {
            var caption = Text(model.Bars[index].Caption, 13, primary, bold);
            caption.MaxTextWidth = Math.Max(10, width);
            caption.MaxLineCount = 1;
            caption.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(caption, new Point(0, 2));
        }
    }

    private FontFamily TextElementFont() => System.Windows.Documents.TextElement.GetFontFamily(this) ?? SystemFonts.MessageFontFamily;

    private static double Snap(double y) => Math.Round(y) + 0.5;

    /// <summary>A column: 4 px rounded at the data end, square at the baseline.</summary>
    internal static Geometry RoundedTop(double x, double y, double w, double h)
    {
        var r = Math.Min(4, Math.Min(w / 2, h));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x, y + h), true, true);
            ctx.LineTo(new Point(x, y + r), false, false);
            ctx.ArcTo(new Point(x + r, y), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(x + w - r, y), false, false);
            ctx.ArcTo(new Point(x + w, y + r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(x + w, y + h), false, false);
        }
        geometry.Freeze();
        return geometry;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = _geometry?.IndexAt(e.GetPosition(this).X) ?? -1;
        var model = Model;
        if (index >= 0 && (model is null || index >= model.Bars.Count || model.Bars[index].IsEmpty)) index = -1;
        Cursor = index >= 0 && model?.Bars[index].Command is not null ? Cursors.Hand : null;
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

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_hover < 0 || Model is not { } model || _hover >= model.Bars.Count) return;
        if (model.Bars[_hover].Command is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
