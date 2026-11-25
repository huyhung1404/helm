using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Helm.Core.Ui;

/// <summary>
/// Colours a control by where it sits on Helm's background: the colour of the background's corner glows at that spot
/// (blue top left, cyan top right, sky bottom left, teal bottom right, <see cref="BackdropSpec"/>), mixed by distance.
/// A switch that is on picks up the colour of the page around it, so switches at the top left of a page are bluer and
/// those at the bottom right greener. Every ToggleSwitch has it (HelmTheme).
/// </summary>
public static class BackdropTint
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("Enabled");

    // The icon's corners, the same colours as the background's glows.
    private static readonly Color TopLeft = Color.FromUInt32(0xFF0077F2);
    private static readonly Color TopRight = Color.FromUInt32(0xFF00C6E0);
    private static readonly Color BottomLeft = Color.FromUInt32(0xFF06AEFF);
    private static readonly Color BottomRight = Color.FromUInt32(0xFF00CFC0);

    static BackdropTint() => EnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
    {
        if (e.NewValue is not true) return;
        control.AttachedToVisualTree += (_, _) => Update(control);
        control.EffectiveViewportChanged += (_, _) => Update(control);
    });

    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);

    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);

    /// <summary>The background's colour at a point given as fractions of the surface (0,0 top left; 1,1 bottom right).</summary>
    public static Color At(double x, double y)
    {
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        static byte Mix(byte tl, byte tr, byte bl, byte br, double x, double y) =>
            (byte)Math.Round((tl * (1 - x) + tr * x) * (1 - y) + (bl * (1 - x) + br * x) * y);
        return Color.FromRgb(
            Mix(TopLeft.R, TopRight.R, BottomLeft.R, BottomRight.R, x, y),
            Mix(TopLeft.G, TopRight.G, BottomLeft.G, BottomRight.G, x, y),
            Mix(TopLeft.B, TopRight.B, BottomLeft.B, BottomRight.B, x, y));
    }

    private static void Update(Control control)
    {
        if (TopLevel.GetTopLevel(control) is not { } top || control.Bounds.Width <= 0) return;
        // The surface the background spans: the whole window inside the WPF shell, a stand-in screen (the Gallery's phone
        // frame), or the window.
        Control surface = control.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.Classes.Contains("screen")) ?? top;
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), surface);
        if (centre is not { } p) return;
        var area = Backdrop.GetCanvas(control) is { } canvas && ReferenceEquals(surface, top) ? canvas : new Rect(surface.Bounds.Size);
        if (area.Width <= 0 || area.Height <= 0) return;
        var colour = At((p.X - area.X) / area.Width, (p.Y - area.Y) / area.Height);
        if (control.Resources.TryGetValue("ToggleSwitchFillOn", out var old) && old is ISolidColorBrush { } brush && brush.Color == colour) return;
        var hover = Lighten(colour, 0.18);
        var pressed = Lighten(colour, -0.15);
        control.Resources["ToggleSwitchFillOn"] = new SolidColorBrush(colour);
        control.Resources["ToggleSwitchStrokeOn"] = new SolidColorBrush(colour);
        control.Resources["ToggleSwitchFillOnPointerOver"] = new SolidColorBrush(hover);
        control.Resources["ToggleSwitchStrokeOnPointerOver"] = new SolidColorBrush(hover);
        control.Resources["ToggleSwitchFillOnPressed"] = new SolidColorBrush(pressed);
        control.Resources["ToggleSwitchStrokeOnPressed"] = new SolidColorBrush(pressed);
    }

    /// <summary>Towards white (amount &gt; 0) or black (amount &lt; 0).</summary>
    private static Color Lighten(Color c, double amount)
    {
        byte Step(byte v) => (byte)Math.Round(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount));
        return Color.FromRgb(Step(c.R), Step(c.G), Step(c.B));
    }
}
