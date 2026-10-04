using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Helm.Modules.Wallet;

/// <summary>
/// The budget meter, drawn like the Windows one: the spent share on a lighter track of the same blue (red once over
/// budget, always with the words "Over budget" beside it), and a thin mark at today's share of the month.
/// </summary>
public sealed class BudgetMeter : Control
{
    public static readonly StyledProperty<double> PercentProperty = AvaloniaProperty.Register<BudgetMeter, double>(nameof(Percent));

    public static readonly StyledProperty<double> PaceProperty = AvaloniaProperty.Register<BudgetMeter, double>(nameof(Pace), -1);

    public static readonly StyledProperty<bool> IsOverProperty = AvaloniaProperty.Register<BudgetMeter, bool>(nameof(IsOver));

    static BudgetMeter()
    {
        AffectsRender<BudgetMeter>(PercentProperty, PaceProperty, IsOverProperty);
    }

    public BudgetMeter()
    {
        Height = 18;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Spent, 0–100.</summary>
    public double Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    /// <summary>Today's share of the month, 0–100; below 0 for none.</summary>
    public double Pace
    {
        get => GetValue(PaceProperty);
        set => SetValue(PaceProperty, value);
    }

    public bool IsOver
    {
        get => GetValue(IsOverProperty);
        set => SetValue(IsOverProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var dark = WalletTheme.IsDark(this);
        var w = Bounds.Width;
        const double bar = 10;
        var top = (Bounds.Height - bar) / 2;
        context.DrawRectangle(WalletTheme.Brush(WalletPalette.Track(dark)), null, new Rect(0, top, w, bar), bar / 2, bar / 2);
        var fill = Math.Clamp(Percent, 0, 100) / 100 * w;
        if (fill > 0)
            context.DrawRectangle(WalletTheme.Brush(IsOver ? WalletPalette.Critical : WalletPalette.Accent(dark)), null,
                new Rect(0, top, Math.Max(bar, fill), bar), bar / 2, bar / 2);
        if (Pace >= 0)
        {
            var x = Math.Round(Math.Clamp(Pace, 0, 100) / 100 * w);
            context.DrawRectangle(WalletTheme.Primary(this), null, new Rect(Math.Clamp(x - 1, 0, Math.Max(0, w - 2)), 0, 2, Bounds.Height));
        }
    }
}
