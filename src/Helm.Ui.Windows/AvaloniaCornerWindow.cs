using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Desktop;

namespace Helm.Ui.Windows;

/// <summary>
/// A see-through Avalonia window over every app that never takes the keyboard (the Vocabulary card, Slack Report's
/// list): in the bottom-right corner of the primary screen's work area, or wherever the user dragged it, growing and
/// shrinking from its bottom edge. A spot off every screen (a monitor unplugged) is pulled back in. The content draws
/// its own card; <see cref="ShadowSpace"/> around it is left see-through for its shadow.
/// </summary>
/// <remarks>
/// Win32 extended styles on its HWND, as the WPF windows had: WS_EX_NOACTIVATE (a click never takes the focus from the
/// app in front) and WS_EX_TOOLWINDOW (kept out of Alt+Tab), through <see cref="IWindowService.MakeNonActivating"/>;
/// Avalonia's <c>Topmost</c> adds WS_EX_TOPMOST. It is not click-through (no WS_EX_TRANSPARENT): its buttons are used.
/// While a box on it is typed in, <see cref="TakeKeyboard"/> lifts WS_EX_NOACTIVATE and <see cref="ReturnKeyboard"/>
/// gives the keyboard back to the app that had it.
/// </remarks>
public sealed class AvaloniaCornerWindow
{
    /// <summary>Room around the card for its shadow (device-independent pixels).</summary>
    public const double ShadowSpace = 16;

    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly Action<Point?> _moved;
    private readonly Window _window;
    private Point? _anchor;
    private nint _previous;
    private bool _typing;

    /// <param name="width">The window's width (the card's plus <see cref="ShadowSpace"/> each side); null: as wide as its content.</param>
    /// <param name="anchor">
    /// Where the user put it last: its bottom-left corner in the primary screen's device-independent pixels (as the WPF
    /// windows kept it); null for the bottom-right corner.
    /// </param>
    /// <param name="moved">Keeps the new bottom-left corner after a drag (null after going back to the corner).</param>
    public AvaloniaCornerWindow(IWindowService windows, IMonitorService monitors, string title, double? width, Point? anchor, Action<Point?> moved)
    {
        HelmAvalonia.EnsureStarted();
        _windows = windows;
        _monitors = monitors;
        _anchor = anchor;
        _moved = moved;
        _window = new Window
        {
            Title = title,
            SizeToContent = width is null ? SizeToContent.WidthAndHeight : SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        if (width is { } w) _window.Width = w;
        MakeNonActivating();
        // It grows and shrinks from its bottom edge (an answer shown, items come and go).
        _window.SizeChanged += (_, _) => Place();
        _window.Closed += (_, _) =>
        {
            ReturnKeyboard();
            Closed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>What the window shows: the card (give it a margin of <see cref="ShadowSpace"/>).</summary>
    public Control? Content
    {
        get => _window.Content as Control;
        set => _window.Content = value;
    }

    /// <summary>Raised once the window is gone, however it was closed.</summary>
    public event EventHandler? Closed;

    public bool IsVisible => _window.IsVisible;

    /// <summary>The window's HWND (0 before it exists).</summary>
    public nint Handle => _window.TryGetPlatformHandle()?.Handle ?? 0;

    /// <summary>The height of the work area of the monitor it is on, in its device-independent pixels.</summary>
    public double WorkAreaHeight =>
        _monitors.FromWindow(Handle) is { } monitor ? monitor.WorkArea.Height / monitor.Scale : 720;

    /// <summary>Shows it in its place without taking the focus.</summary>
    public void Show()
    {
        if (_window.IsVisible) return;
        // Off-screen first, so it never flashes at the wrong spot before its size is known.
        _window.Position = new PixelPoint(-32000, -32000);
        _window.Show();
        MakeNonActivating();
        Place();
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
    }

    /// <summary>Hides it (kept for the next <see cref="Show"/>).</summary>
    public void Hide()
    {
        ReturnKeyboard();
        _window.Hide();
    }

    public void Close() => _window.Close();

    /// <summary>The card was pressed: it moves with the mouse (the system's own move loop). True when it moved, false for a click.</summary>
    public bool Drag(PointerPressedEventArgs e)
    {
        var before = _window.Position;
        _window.BeginMoveDrag(e);
        var after = _window.Position;
        if (Math.Abs(after.X - before.X) < 3 && Math.Abs(after.Y - before.Y) < 3) return false;
        var scale = PrimaryScale();
        var height = _window.Bounds.Height * _window.RenderScaling;
        _anchor = new Point(after.X / scale, (after.Y + height) / scale);
        _moved(_anchor);
        return true;
    }

    /// <summary>Back to the bottom-right corner (a double-click).</summary>
    public void BackToCorner()
    {
        _anchor = null;
        _moved(null);
        Place();
    }

    /// <summary>A box on it is used: it takes the keyboard from the app in front, which gets it back afterwards.</summary>
    public void TakeKeyboard()
    {
        if (Handle is not (var hwnd and not 0)) return;
        if (!_typing)
        {
            _typing = true;
            _previous = _windows.GetForegroundWindow();
            _windows.AllowActivation(hwnd, true);
        }
        _window.Activate();
        _windows.Activate(hwnd);
    }

    /// <summary>Done typing: non-activating again, and the keyboard back to the app that had it.</summary>
    public void ReturnKeyboard()
    {
        if (!_typing) return;
        _typing = false;
        var hwnd = Handle;
        if (hwnd != 0) _windows.AllowActivation(hwnd, false);
        if (_previous != 0 && _previous != hwnd) _windows.Activate(_previous);
        _previous = 0;
    }

    private void MakeNonActivating()
    {
        if (Handle is var hwnd and not 0) _windows.MakeNonActivating(hwnd);
    }

    private double PrimaryScale() => _monitors.GetMonitors().FirstOrDefault(m => m.IsPrimary)?.Scale ?? _window.RenderScaling;

    private void Place()
    {
        if (!_window.IsVisible || Handle is not (var hwnd and not 0)) return;
        var monitors = _monitors.GetMonitors();
        if ((monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault()) is not { } primary) return;
        var width = (int)Math.Round(_window.Bounds.Width * _window.RenderScaling);
        var height = (int)Math.Round(_window.Bounds.Height * _window.RenderScaling);
        int x, y;
        if (_anchor is not { } at)
        {
            var work = primary.WorkArea;
            x = work.Right - width;
            y = work.Bottom - height;
        }
        else
        {
            var left = monitors.Min(m => m.Bounds.Left);
            var top = monitors.Min(m => m.Bounds.Top);
            var right = monitors.Max(m => m.Bounds.Right);
            var bottom = monitors.Max(m => m.Bounds.Bottom);
            x = Math.Clamp((int)Math.Round(at.X * primary.Scale), left, Math.Max(left, right - width));
            y = Math.Clamp((int)Math.Round(at.Y * primary.Scale) - height, top, Math.Max(top, bottom - height));
        }
        _windows.SetWindowPosition(hwnd, new Helm.Core.Geometry.PixelPoint(x, y));
    }
}
