using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Core.Geometry;

namespace Helm.Ui.Windows;

/// <summary>
/// A small borderless Avalonia window that floats over any app, for a shortcut-opened box (Quick Capture): it appears on the
/// monitor under the mouse a fifth down the screen, takes the keyboard, and hides on Esc or when something else is clicked.
/// It is a window of its own, not a page inside a WPF one (a WPF window cannot show an Avalonia child when it is transparent).
/// The content draws its own rounded card; <see cref="ShadowSpace"/> around it is left see-through for its shadow.
/// </summary>
public sealed class AvaloniaFloatingPanel
{
    /// <summary>Room around the card for its shadow (device-independent pixels).</summary>
    public const double ShadowSpace = 16;

    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly Window _window;

    public AvaloniaFloatingPanel(IWindowService windows, IMonitorService monitors, double width, string title)
    {
        _windows = windows;
        _monitors = monitors;
        _window = new Window
        {
            Title = title,
            Width = width + 2 * ShadowSpace,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = true,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        _window.Deactivated += (_, _) => Dismiss();
        _window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            Dismiss();
        };
    }

    /// <summary>What the panel shows: the card (give it a margin of <see cref="ShadowSpace"/>).</summary>
    public Control? Panel
    {
        get => _window.Content as Control;
        set => _window.Content = value;
    }

    /// <summary>Raised after the panel was hidden (Esc, a click elsewhere, or <see cref="Dismiss"/>).</summary>
    public event EventHandler? Dismissed;

    public bool IsVisible => _window.IsVisible;

    /// <summary>Shows the panel on the monitor under the mouse and gives it the keyboard.</summary>
    public void Summon()
    {
        if (!_window.IsVisible)
        {
            // Placed off-screen first, so it never flashes at the old spot before moving.
            _window.Position = new Avalonia.PixelPoint(-32000, -32000);
            _window.Show();
        }
        Place();
        // The panel moved to the monitor's DPI and may have a new size: centre it again once laid out.
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
        _window.Activate();
        // Bring to front even when another app has the foreground (the shortcut gives Helm the right to).
        if (_window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != 0) _windows.Activate(hwnd);
        Summoned?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the panel is shown: put the cursor where typing starts.</summary>
    public event EventHandler? Summoned;

    /// <summary>Hides the panel (kept for the next <see cref="Summon"/>).</summary>
    public void Dismiss()
    {
        if (!_window.IsVisible) return;
        _window.Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void Place()
    {
        if (_window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;
        if (_monitors.FromPoint(_windows.GetCursorPosition()) is not { } monitor) return;
        var scale = monitor.Dpi / 96.0;
        var width = (int)Math.Round(_window.Bounds.Width * scale);
        var work = monitor.WorkArea;
        var x = work.Left + Math.Max(0, (work.Width - width) / 2);
        // A fifth down the screen, like the Windows search and PowerToys Run.
        var y = work.Top + work.Height / 5 - (int)Math.Round(ShadowSpace * scale);
        _windows.SetWindowPosition(hwnd, new Helm.Core.Geometry.PixelPoint(x, y));
    }
}
