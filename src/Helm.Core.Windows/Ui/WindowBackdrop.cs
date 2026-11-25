using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace Helm.Core.Ui;

/// <summary>
/// Helm's full-window background (<see cref="BackdropSpec"/>) behind the WPF shell: title bar, menu, tabs and pages.
/// Shared Avalonia pages paint the same background lined up with the window (Helm.Ui Backdrop), so it reads as one
/// surface. Goes away with the WPF shell.
/// </summary>
public sealed class WindowBackdrop : Grid
{
    private readonly Border _base = new();
    private readonly List<Border> _glows = [];
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = BackdropSpec.Tick };
    private BackdropSpec.Look _look = BackdropSpec.Dark;

    public WindowBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Children.Add(_base);
        foreach (var _ in BackdropSpec.Dark.Glows)
        {
            var glow = new Border { RenderTransform = new TranslateTransform(), CacheMode = new BitmapCache(), Margin = new Thickness(-BackdropSpec.Bleed) };
            _glows.Add(glow);
            Children.Add(glow);
        }
        _timer.Tick += (_, _) => Move();
        Loaded += (_, _) =>
        {
            Paint();
            ApplicationThemeManager.Changed += OnThemeChanged;
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            ApplicationThemeManager.Changed -= OnThemeChanged;
            _timer.Stop();
        };
        SizeChanged += (_, _) => Paint();
    }

    private void OnThemeChanged(ApplicationTheme theme, Color accent) => Paint();

    private void Move()
    {
        var now = Environment.TickCount64;
        for (var i = 0; i < _glows.Count; i++)
        {
            var (x, y) = BackdropSpec.Drift(_look.Glows[i], now);
            var shift = (TranslateTransform)_glows[i].RenderTransform;
            shift.X = x;
            shift.Y = y;
        }
    }

    private void Paint()
    {
        _look = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light ? BackdropSpec.Light : BackdropSpec.Dark;
        _base.Background = new LinearGradientBrush(ToColor(_look.Top), ToColor(_look.Bottom), new Point(0, 0), new Point(0.6, 1));
        for (var i = 0; i < _glows.Count; i++) _glows[i].Background = Glow(_look.Glows[i]);
    }

    /// <summary>Same placement as the Avalonia side: on a layer the size of the window grown by the bleed.</summary>
    private RadialGradientBrush Glow(BackdropSpec.Glow glow)
    {
        double cw = Math.Max(1, ActualWidth), ch = Math.Max(1, ActualHeight);
        var w = cw + 2 * BackdropSpec.Bleed;
        var h = ch + 2 * BackdropSpec.Bleed;
        var center = new Point((BackdropSpec.Bleed + glow.CenterX * cw) / w, (BackdropSpec.Bleed + glow.CenterY * ch) / h);
        var radius = glow.Radius * Math.Max(cw, ch);
        var color = ToColor(glow.Color);
        var brush = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B))
        {
            Center = center,
            GradientOrigin = center,
            RadiusX = radius / w,
            RadiusY = radius / h,
        };
        brush.Freeze();
        return brush;
    }

    private static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
