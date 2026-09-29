using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Helm.Core.Desktop;
using Helm.Core.Geometry;

namespace Helm.Core.Ui;

/// <summary>
/// A borderless panel that pops up over every app from a global shortcut (Quick Capture, the command palette): a
/// fixed width, the height of its content, near the top of the monitor under the mouse, in the app's theme. Esc or a
/// click elsewhere hides it. The window is reused: <see cref="Summon"/> shows it again and brings it to the front.
/// </summary>
public class FloatingPanelWindow : Window
{
    private const double ShadowSpace = 16;
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly Border _surface;

    public FloatingPanelWindow(IWindowService windows, IMonitorService monitors, double width)
    {
        _windows = windows;
        _monitors = monitors;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = width + 2 * ShadowSpace;
        ShowActivated = true;
        UseLayoutRounding = true;

        _surface = new Border
        {
            Margin = new Thickness(ShadowSpace),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Direction = 270 },
        };
        _surface.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        _surface.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        // A window's text does not inherit the theme colour: without this it is black in the dark theme.
        _surface.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        base.Content = _surface;

        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Dismiss();
        };
    }

    /// <summary>What the panel shows (inside its rounded, shadowed surface).</summary>
    public UIElement? Panel
    {
        get => _surface.Child;
        set => _surface.Child = value;
    }

    /// <summary>Raised after the panel was hidden (Esc, a click elsewhere, or <see cref="Dismiss"/>).</summary>
    public event EventHandler? Dismissed;

    /// <summary>Shows the panel on the monitor under the mouse and gives it the keyboard.</summary>
    public void Summon()
    {
        if (!IsVisible)
        {
            // Placed off-screen first, so it never flashes at the old spot before moving.
            Left = -32000;
            Top = -32000;
            Show();
        }
        Place();
        // The panel moved to the monitor's DPI and may have a new size: centre it again once laid out.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Place);
        Activate();
        // Bring to front even when another app has the foreground (the shortcut gives Helm the right to).
        var hwnd = new WindowInteropHelper(this).Handle;
        _windows.Activate(hwnd);
        Keyboard.Focus(this);
        OnSummoned();
    }

    /// <summary>Hides the panel (kept for the next <see cref="Summon"/>).</summary>
    public void Dismiss()
    {
        if (!IsVisible) return;
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called after the panel is shown: put the cursor where typing starts.</summary>
    protected virtual void OnSummoned()
    {
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || _monitors.FromPoint(_windows.GetCursorPosition()) is not { } monitor) return;
        var scale = monitor.Dpi / 96.0;
        var width = (int)Math.Round(ActualWidth * scale);
        var work = monitor.WorkArea;
        var x = work.Left + Math.Max(0, (work.Width - width) / 2);
        // A fifth down the screen, like the Windows search and PowerToys Run.
        var y = work.Top + work.Height / 5 - (int)Math.Round(ShadowSpace * scale);
        _windows.SetWindowPosition(hwnd, new PixelPoint(x, y));
    }
}
