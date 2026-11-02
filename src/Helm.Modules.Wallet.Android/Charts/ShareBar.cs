using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Helm.Modules.Wallet;

/// <summary>
/// The month's spending by category as one bar: each slice of the donut a stretch in its colour, a thin gap between
/// them. The legend under it names every slice, so colour never carries meaning alone.
/// </summary>
public sealed class ShareBar : Control
{
    public static readonly StyledProperty<DonutModel?> ModelProperty = AvaloniaProperty.Register<ShareBar, DonutModel?>(nameof(Model));

    static ShareBar()
    {
        AffectsRender<ShareBar>(ModelProperty);
    }

    public ShareBar()
    {
        Height = 12;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public DonutModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Model is not { IsEmpty: false } model) return;
        var dark = WalletTheme.IsDark(this);
        var w = Bounds.Width;
        var h = Bounds.Height;
        const double gap = 2;
        var room = w - gap * (model.Segments.Count - 1);
        var x = 0.0;
        using (context.PushClip(new RoundedRect(new Rect(0, 0, w, h), h / 2)))
        {
            foreach (var s in model.Segments)
            {
                var width = Math.Max(2, s.Share * room);
                context.DrawRectangle(WalletTheme.Category(s.Color, 0xFF, dark), null, new Rect(x, 0, width, h));
                x += width + gap;
            }
        }
    }
}
