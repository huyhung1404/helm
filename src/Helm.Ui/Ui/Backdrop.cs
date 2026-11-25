using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Helm.Core.Ui;

/// <summary>
/// The full-bleed surface of a glass page: a warm vertical gradient with two soft glows (top left, bottom right), in
/// Helm's light or dark colours (HelmBackdrop*, HelmGlow*). Put it behind the page's content; frosted cards
/// (Border.glass) sit on it.
/// </summary>
public sealed class Backdrop : Panel
{
    private readonly Border _base = new();
    private readonly Border _warm = new();
    private readonly Border _soft = new();

    public Backdrop()
    {
        IsHitTestVisible = false;
        Children.Add(_base);
        Children.Add(_warm);
        Children.Add(_soft);
        ActualThemeVariantChanged += (_, _) => Paint();
        AttachedToVisualTree += (_, _) => Paint();
    }

    private Color Find(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color c ? c : fallback;

    private void Paint()
    {
        var top = Find("HelmBackdropTop", Colors.Black);
        var bottom = Find("HelmBackdropBottom", Colors.Black);
        _base.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.6, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
        _warm.Background = Glow(Find("HelmGlowWarm", Colors.Transparent), 0.18, 0.05, 0.75);
        _soft.Background = Glow(Find("HelmGlowSoft", Colors.Transparent), 0.85, 0.95, 0.7);
    }

    private static RadialGradientBrush Glow(Color color, double x, double y, double radius) => new()
    {
        Center = new RelativePoint(x, y, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(x, y, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(radius, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(radius, RelativeUnit.Relative),
        GradientStops = { new GradientStop(color, 0), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1) },
    };
}
