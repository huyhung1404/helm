using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Helm.Core.Ui;

/// <summary>
/// The full-bleed background of a glass page (<see cref="BackdropSpec"/>): put it behind the page's content; frosted
/// cards (Border.glass) sit on it. Inside the WPF shell the host sets <see cref="CanvasProperty"/> to the whole
/// window, so this page paints its part of the window's background and meets the shell's without a seam.
/// </summary>
public sealed class Backdrop : Panel
{
    /// <summary>The surface the background spans, in this control's coordinates (null: just this control).</summary>
    public static readonly AttachedProperty<Rect?> CanvasProperty =
        AvaloniaProperty.RegisterAttached<Backdrop, Control, Rect?>("Canvas", inherits: true);

    public static void SetCanvas(Control control, Rect? value) => control.SetValue(CanvasProperty, value);

    public static Rect? GetCanvas(Control control) => control.GetValue(CanvasProperty);

    private readonly Border _base = new();
    private readonly List<Border> _glows = [];
    private BackdropSpec.Look _look = BackdropSpec.Dark;
    private DispatcherTimer? _timer;

    static Backdrop() => AffectsArrange<Backdrop>(CanvasProperty);

    public Backdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Children.Add(_base);
        foreach (var _ in BackdropSpec.Dark.Glows)
        {
            var glow = new Border { RenderTransform = new TranslateTransform() };
            _glows.Add(glow);
            Children.Add(glow);
        }
        ActualThemeVariantChanged += (_, _) => Paint();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Paint();
        // The page breathes: the glows drift slowly (tens of seconds), in step with the shell's (same clock).
        _timer = new DispatcherTimer(BackdropSpec.Tick, DispatcherPriority.Background, (_, _) => Move());
        _timer.Start();
        Move();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var canvas = GetCanvas(this) ?? new Rect(finalSize);
        _base.Arrange(canvas);
        var layer = canvas.Inflate(BackdropSpec.Bleed);
        foreach (var glow in _glows) glow.Arrange(layer);
        return finalSize;
    }

    private void Move()
    {
        var now = Environment.TickCount64;
        for (var i = 0; i < _glows.Count; i++)
        {
            var (x, y) = BackdropSpec.Drift(_look.Glows[i], now);
            var shift = (TranslateTransform)_glows[i].RenderTransform!;
            shift.X = x;
            shift.Y = y;
        }
    }

    private void Paint()
    {
        _look = ActualThemeVariant == ThemeVariant.Light ? BackdropSpec.Light : BackdropSpec.Dark;
        _base.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.6, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromUInt32(_look.Top), 0), new GradientStop(Color.FromUInt32(_look.Bottom), 1) },
        };
        for (var i = 0; i < _glows.Count; i++) _glows[i].Background = Glow(_look.Glows[i]);
    }

    /// <summary>A glow placed on the glow layer, which is the canvas grown by <see cref="BackdropSpec.Bleed"/> each side.</summary>
    private RadialGradientBrush Glow(BackdropSpec.Glow glow)
    {
        var canvas = GetCanvas(this) ?? new Rect(Bounds.Size);
        var w = Math.Max(1, canvas.Width + 2 * BackdropSpec.Bleed);
        var h = Math.Max(1, canvas.Height + 2 * BackdropSpec.Bleed);
        var center = new Point(BackdropSpec.Bleed + glow.CenterX * canvas.Width, BackdropSpec.Bleed + glow.CenterY * canvas.Height);
        var radius = glow.Radius * Math.Max(canvas.Width, canvas.Height);
        var color = Color.FromUInt32(glow.Color);
        return new RadialGradientBrush
        {
            Center = new RelativePoint(center.X / w, center.Y / h, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(center.X / w, center.Y / h, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(radius / w, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(radius / h, RelativeUnit.Relative),
            GradientStops = { new GradientStop(color, 0), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1) },
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The glows are placed in pixels of the canvas: repaint when it (or this control, without a canvas) resizes.
        if (change.Property == CanvasProperty || change.Property == BoundsProperty) Paint();
    }
}
