using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.Wallet;

/// <summary>
/// The budget meter: the spent share as a fill on a lighter track of the same blue (red once over budget, always with
/// the words "Over budget" beside it), and a thin mark at today's share of the month, where spending would be on track.
/// </summary>
public sealed class BudgetMeter : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(BudgetMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaceProperty = DependencyProperty.Register(
        nameof(Pace), typeof(double), typeof(BudgetMeter), new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsOverProperty = DependencyProperty.Register(
        nameof(IsOver), typeof(bool), typeof(BudgetMeter), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Spent, 0–100.</summary>
    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    /// <summary>Today's share of the month, 0–100; below 0 for none.</summary>
    public double Pace
    {
        get => (double)GetValue(PaceProperty);
        set => SetValue(PaceProperty, value);
    }

    public bool IsOver
    {
        get => (bool)GetValue(IsOverProperty);
        set => SetValue(IsOverProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 18);

    protected override void OnRender(DrawingContext dc)
    {
        var dark = WalletTheme.IsDark(this);
        var w = ActualWidth;
        const double bar = 10;
        var top = (ActualHeight - bar) / 2;
        dc.DrawRoundedRectangle(WalletTheme.Frozen(WalletPalette.Track(dark)), null, new Rect(0, top, w, bar), bar / 2, bar / 2);
        var fill = Math.Clamp(Percent, 0, 100) / 100 * w;
        if (fill > 0)
        {
            var color = IsOver ? WalletPalette.Critical : WalletPalette.Accent(dark);
            dc.DrawRoundedRectangle(WalletTheme.Frozen(color), null, new Rect(0, top, Math.Max(bar, fill), bar), bar / 2, bar / 2);
        }
        if (Pace >= 0)
        {
            var x = Math.Round(Math.Clamp(Pace, 0, 100) / 100 * w);
            var ink = WalletTheme.Text(this, "TextFillColorPrimaryBrush", Brushes.Black);
            dc.DrawRectangle(ink, null, new Rect(Math.Clamp(x - 1, 0, Math.Max(0, w - 2)), 0, 2, ActualHeight));
        }
    }
}
