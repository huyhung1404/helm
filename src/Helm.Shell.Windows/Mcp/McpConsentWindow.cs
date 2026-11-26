using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Core.Mcp;
using Helm.Mcp.Ui;
using Helm.Ui.Windows;

namespace Helm.Shell.Windows.Mcp;

/// <summary>
/// The question before an AI agent's tool call runs: the shared <see cref="McpConsentView"/> as a glass sheet in a
/// see-through Avalonia window of its own, centred on the monitor under the mouse, on top of everything and given the
/// keyboard, whether Helm's main window is shown or hidden in the tray (the agent's terminal usually has the focus, and a
/// question that is not topmost could wait unseen behind it). Its answer is Deny unless an Allow button is pressed;
/// closing it any way (Alt+F4, <see cref="Close"/>) denies.
/// </summary>
/// <remarks>
/// A normal activating window with a taskbar button (so it can be found again), with Avalonia's <c>Topmost</c>
/// (WS_EX_TOPMOST). No WS_EX_NOACTIVATE: the user answers it with the keyboard.
/// </remarks>
public sealed class McpConsentWindow
{
    /// <summary>The sheet's width; the window adds room for its shadow all round.</summary>
    private const double CardWidth = 520;

    /// <summary>See-through room around the sheet for its shadow (device-independent pixels).</summary>
    private const double ShadowSpace = 16;

    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly Window _window;
    private McpConsentAnswer _answer = McpConsentAnswer.Deny;

    public McpConsentWindow(McpConsentPrompt prompt, IWindowService windows, IMonitorService monitors)
    {
        _windows = windows;
        _monitors = monitors;
        var view = new McpConsentView(new McpConsentText(prompt)) { Margin = new Thickness(ShadowSpace) };
        _window = new Window
        {
            Title = "An AI agent asks for your OK",
            Width = CardWidth + 2 * ShadowSpace,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = true,
            Topmost = true,
            ShowActivated = true,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = view,
        };
        view.Answered += (_, answer) =>
        {
            _answer = answer;
            _window.Close();
        };
        view.DragRequested += (_, e) => _window.BeginMoveDrag(e);
        // Enter or Escape before anything in the sheet has the keyboard: Deny too.
        _window.KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Escape)) return;
            e.Handled = true;
            view.Deny();
        };
        _window.Closed += (_, _) => Closed?.Invoke(this, _answer);
    }

    /// <summary>Raised once the window is gone, with the answer (Deny unless an Allow button was pressed).</summary>
    public event EventHandler<McpConsentAnswer>? Closed;

    public bool IsVisible => _window.IsVisible;

    /// <summary>Shows it centred on the monitor under the mouse, on top, with the keyboard.</summary>
    public void Show()
    {
        // Placed off-screen first, so it never flashes at the old spot before moving.
        _window.Position = new PixelPoint(-32000, -32000);
        _window.Show();
        Place();
        // Laid out at the monitor's DPI now: centre it again with its real size.
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
        Activate();
    }

    /// <summary>Brings it to the front, even when another app has the foreground.</summary>
    public void Activate()
    {
        _window.Activate();
        if (_window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != 0) _windows.Activate(hwnd);
    }

    /// <summary>Closes it unanswered (out of time, or the agent left): Deny.</summary>
    public void Close() => _window.Close();

    private void Place()
    {
        if (_window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;
        if (_monitors.FromPoint(_windows.GetCursorPosition()) is not { } monitor) return;
        var scale = monitor.Scale;
        var work = monitor.WorkArea;
        // Never taller than the monitor: a long question scrolls inside.
        _window.MaxHeight = Math.Max(320, work.Height / scale - 40);
        var width = (int)Math.Round(_window.Bounds.Width * scale);
        var height = (int)Math.Round(_window.Bounds.Height * scale);
        var x = work.Left + Math.Max(0, (work.Width - width) / 2);
        var y = work.Top + Math.Max(0, (work.Height - height) / 2);
        _windows.SetWindowPosition(hwnd, new Helm.Core.Geometry.PixelPoint(x, y));
    }
}
