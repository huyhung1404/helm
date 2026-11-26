using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Services;
using Helm.Modules.WindowZones.Ui;
using Helm.Ui.Windows;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.WindowZones.Engine;

/// <summary>
/// The tab strip: shown at the top of the zone when you switch windows or open the zone, gone a moment after the last
/// switch (it stays while the pointer is on it). It never takes the focus; clicking a tab brings that window forward
/// (<see cref="TabChosen"/>). The engine calls it from its own thread: everything is marshalled to the UI thread, which
/// is also Avalonia's.
/// </summary>
public sealed class ZoneTabStrip(IUiDispatcher ui, IWindowService windows, ILogger<ZoneTabStrip> logger) : IZoneTabStrip
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromMilliseconds(1300);

    private TabStripWindow? _window;
    private DispatcherTimer? _timer;

    /// <summary>A tab was clicked (UI thread): the module brings that window forward in its zone.</summary>
    public event EventHandler<nint>? TabChosen;

    public void Show(TabStripView view) => ui.Post(() =>
    {
        try
        {
            if (_window is null)
            {
                _window = new TabStripWindow(windows);
                _window.TabChosen += (_, handle) =>
                {
                    HideNow();
                    TabChosen?.Invoke(this, handle);
                };
                // Reading the tabs or reaching for one: keep them until the pointer leaves.
                _window.PointerInside += (_, inside) =>
                {
                    _timer?.Stop();
                    if (!inside) _timer?.Start();
                };
            }
            _window.ShowStrip(view);
            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = ShowFor };
                _timer.Tick += (_, _) => HideNow();
            }
            _timer.Stop();
            _timer.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not show the zone's tabs");
        }
    });

    public void Hide() => ui.Post(HideNow);

    private void HideNow()
    {
        _timer?.Stop();
        _window?.Hide();
    }
}

/// <summary>
/// The see-through Avalonia window around <see cref="ZoneTabStripView"/>: topmost, out of the taskbar and Alt+Tab, and
/// never activated (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW), so the window you work in keeps the keyboard even when a tab
/// is clicked. Centred on the zone's top edge, just inside it (over the title bar of the window in front).
/// </summary>
internal sealed class TabStripWindow
{
    private readonly IWindowService _windows;
    private readonly Window _window;
    private readonly ZoneTabStripView _view = new();
    private TabStripView? _shown;

    public TabStripWindow(IWindowService windows)
    {
        _windows = windows;
        _window = new Window
        {
            Title = "Window Zones tabs",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = _view,
        };
        _view.TabChosen += (_, handle) => TabChosen?.Invoke(this, handle);
        _window.PointerEntered += (_, _) => PointerInside?.Invoke(this, true);
        _window.PointerExited += (_, _) => PointerInside?.Invoke(this, false);
        // Moved onto a monitor with another DPI: laid out again at its size, so centre it again.
        _window.ScalingChanged += (_, _) => Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
    }

    public event EventHandler<nint>? TabChosen;

    public event EventHandler<bool>? PointerInside;

    public bool IsVisible => _window.IsVisible;

    public void ShowStrip(TabStripView view)
    {
        _shown = view;
        _view.Show(view, ProgramIcons.For);
        if (!_window.IsVisible)
        {
            // Off-screen first, so it never flashes at the old spot before moving.
            _window.Position = new Avalonia.PixelPoint(-32000, -32000);
            _window.Show();
            // After Show: Avalonia sets the window's extended styles as it shows it. Never activated, out of Alt+Tab.
            if (Handle is { } hwnd) _windows.MakeNonActivating(hwnd);
        }
        Place();
        // The strip may have moved to a monitor with another DPI and changed size: place it again once laid out.
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
    }

    public void Hide() => _window.Hide();

    private nint? Handle => _window.TryGetPlatformHandle()?.Handle is { } h && h != 0 ? h : null;

    private void Place()
    {
        if (!_window.IsVisible || _shown is not { } view || Handle is not { } hwnd) return;
        var scale = view.Monitor.Scale;
        var width = (int)Math.Round(_window.Bounds.Width * scale);
        var zone = view.Zone;
        var x = zone.Left + Math.Max(0, (zone.Width - width) / 2);
        // Inside the zone, so nothing sticks out onto a monitor above.
        var y = zone.Top + (int)Math.Round(4 * scale);
        _windows.SetWindowPosition(hwnd, new PixelPoint(x, y));
    }
}
