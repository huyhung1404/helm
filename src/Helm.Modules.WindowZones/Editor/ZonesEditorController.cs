using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Core.Services;
using Helm.Modules.WindowZones.Ui;
using Helm.Ui.Windows;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.WindowZones.Editor;

/// <summary>
/// Opens one <see cref="ZoneEditorWindow"/> per monitor on the UI thread, all sharing one <see cref="EditorSession"/>.
/// The monitor under the cursor shows the toolbar; closing one closes all.
/// </summary>
public sealed class ZonesEditorController(
    WindowZonesData data,
    IMonitorService monitors,
    IWindowService windows,
    IUiDispatcher ui,
    ILogger<ZonesEditorController> logger)
{
    private readonly List<ZoneEditorWindow> _open = [];

    public bool IsOpen => _open.Count > 0;

    /// <summary>Callable from any thread (e.g. the hotkey thread).</summary>
    public void Toggle() => ui.Post(() =>
    {
        if (IsOpen) Close();
        else Open();
    });

    public void Show() => ui.Post(() =>
    {
        if (!IsOpen) Open();
    });

    /// <summary>UI thread.</summary>
    public void Close()
    {
        var open = _open.ToList();
        _open.Clear();
        foreach (var w in open) w.Close();
    }

    private void Open()
    {
        try
        {
            var all = monitors.GetMonitors();
            if (all.Count == 0) return;
            var home = monitors.FromPoint(windows.GetCursorPosition()) is { } m ? all.FirstOrDefault(x => x.Id == m.Id) ?? all[0] : all[0];

            var session = new EditorSession(data, all, home);
            session.CloseRequested += (_, _) => ui.Post(Close);

            ZoneEditorWindow? focus = null;
            foreach (var monitor in all)
            {
                var isHome = monitor.Id == home.Id;
                var window = new ZoneEditorWindow(session, monitor, isHome, windows);
                // However one closes (Esc, Alt+F4), all close: the session is shared.
                window.Closed += (_, _) =>
                {
                    if (_open.Remove(window)) ui.Post(Close);
                };
                _open.Add(window);
                window.Show();
                if (isHome) focus = window;
            }
            (focus ?? _open.FirstOrDefault())?.Activate();
            logger.LogInformation("Zone editor opened on {Count} monitor(s), toolbar on {Home}", _open.Count, home.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not open the zone editor");
            Close();
        }
    }
}

/// <summary>
/// The full-screen editor on one monitor: a borderless, topmost, see-through Avalonia window around
/// <see cref="ZoneScreenEditorView"/> that covers the monitor exactly. It is placed in physical pixels (the process is
/// per-monitor-v2 DPI aware) and placed again once Avalonia has followed a DPI change, so it never ends a pixel short.
/// </summary>
internal sealed class ZoneEditorWindow
{
    private readonly MonitorInfo _monitor;
    private readonly IWindowService _windows;
    private readonly Window _window;

    public ZoneEditorWindow(EditorSession session, MonitorInfo monitor, bool isHome, IWindowService windows)
    {
        _monitor = monitor;
        _windows = windows;
        _window = new Window
        {
            Title = "Window Zones editor",
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            // Only the toolbar's monitor takes the keyboard; the others show without taking it from it.
            ShowActivated = isHome,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new ZoneScreenEditorView(session, monitor, isHome),
        };
        // Moving onto a monitor with another DPI makes Avalonia resize the window to Windows' suggestion: cover it again.
        _window.ScalingChanged += (_, _) => Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
        _window.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Closed;

    public void Show()
    {
        // Created on its own monitor first, so it starts at that monitor's DPI.
        _window.Position = new Avalonia.PixelPoint(_monitor.Bounds.Left, _monitor.Bounds.Top);
        _window.Show();
        Place();
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
    }

    /// <summary>Gives the keyboard to this window, even when another app has the foreground (the shortcut allows it).</summary>
    public void Activate()
    {
        _window.Activate();
        if (Handle is { } hwnd) _windows.Activate(hwnd);
    }

    public void Close() => _window.Close();

    private nint? Handle => _window.TryGetPlatformHandle()?.Handle is { } h && h != 0 ? h : null;

    private void Place()
    {
        if (_window.IsVisible && Handle is { } hwnd) _windows.SetWindowBounds(hwnd, _monitor.Bounds);
    }
}
