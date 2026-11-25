using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Helm.Core.Ui;

/// <summary>What <see cref="Sheet.Show"/> opened: close it, or hear that it closed (its X, a tap outside, Escape).</summary>
public sealed class SheetHandle
{
    internal Control? Overlay;

    public event Action? Closed;

    public bool IsOpen => Overlay is not null;

    public void Close()
    {
        if (Overlay is null) return;
        if (Overlay.Parent is Panel panel) panel.Children.Remove(Overlay);
        Overlay = null;
        Closed?.Invoke();
    }
}

/// <summary>
/// Every floating card in Helm (the date picker, a confirmation, a tool's settings): a solid rounded card over a dimmed
/// screen, growing into place. On a phone it is a sheet from the bottom edge; on a wider screen a card in the middle.
/// "Screen" is the window, or a stand-in (the Gallery's phone frame has class <c>screen</c>).
/// </summary>
public static class Sheet
{
    /// <summary>Below this screen width a sheet comes from the bottom (a phone).</summary>
    public const double PhoneBelow = 720;

    private static (Control Screen, TopLevel Top)? Find(Control anchor)
    {
        if (TopLevel.GetTopLevel(anchor) is not { } top) return null;
        Control screen = anchor.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.Classes.Contains("screen")) ?? top;
        return (screen, top);
    }

    /// <summary>True when sheets over <paramref name="anchor"/>'s screen come from the bottom.</summary>
    public static bool IsPhone(Control anchor) => Find(anchor) is { } f && f.Screen.Bounds.Width < PhoneBelow;

    /// <summary>Opens <paramref name="body"/> in a sheet; null when there is no screen to open over.</summary>
    public static SheetHandle? Show(Control anchor, Control body, double width = 400)
    {
        if (Find(anchor) is not { } found || OverlayLayer.GetOverlayLayer(anchor) is not { } layer) return null;
        var (screen, top) = found;
        var at = screen.TranslatePoint(default, top) ?? default;
        var size = screen.Bounds.Size;
        var phone = size.Width < PhoneBelow;
        var handle = new SheetHandle();

        var card = new Border
        {
            Classes = { "sheet" },
            Padding = phone ? new Thickness(18, 10, 18, 24) : new Thickness(22),
            Width = phone ? double.NaN : width,
            VerticalAlignment = phone ? VerticalAlignment.Bottom : VerticalAlignment.Center,
            HorizontalAlignment = phone ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    phone ? new Border { Width = 36, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Colors.Gray, 0.5) } : new Panel { Height = 0 },
                    new ScrollViewer { Content = body, MaxHeight = size.Height - 96, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
                },
            },
        };
        if (phone) card.CornerRadius = new CornerRadius(28, 28, 0, 0);
        // A tap on the card itself must not reach the scrim.
        card.PointerPressed += (_, e) => e.Handled = true;
        var scrim = new Grid { Width = size.Width, Height = size.Height, Children = { card }, Focusable = true };
        scrim.Bind(Panel.BackgroundProperty, anchor.GetResourceObservable("HelmScrim"));
        scrim.PointerPressed += (_, _) => handle.Close();
        scrim.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) handle.Close();
        };

        // The overlay layer is outside the page: keep the page's light/dark (it may differ from the window's, in the Gallery).
        var overlay = new ThemeVariantScope { RequestedThemeVariant = anchor.ActualThemeVariant, Child = scrim };
        Canvas.SetLeft(overlay, at.X);
        Canvas.SetTop(overlay, at.Y);
        handle.Overlay = overlay;
        layer.Children.Add(overlay);

        // The card grows into place; a phone's sheet grows up from its bottom edge.
        card.RenderTransform = new ScaleTransform();
        if (phone) card.RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative);
        Motion.Enter(card, phone ? 0.9 : 0.94);
        return handle;
    }
}
