using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.BugReports.Ui;
using Helm.Core.Desktop;
using Helm.Shell.BugReports;
using Helm.Ui.Windows;

namespace Helm.Shell.Windows;

/// <summary>
/// The report window (Ctrl+Alt+F12, the title bar's bug button, the tray menu): the shared <see cref="BugReportFormView"/> in
/// a see-through Avalonia window of its own, a rounded card with its shadow, over every app like Quick Capture's box. Unlike
/// that box it stays when another window takes the focus (picking a picture, copying something), so nothing typed is
/// lost. Closing it, however it closes, lets the black box record again (the form thaws it).
/// </summary>
/// <remarks>
/// A window of its own, not a WPF window around an <c>AvaloniaHost</c>: a WPF window cannot show an Avalonia child once it is
/// transparent (airspace), and the card needs the see-through margin for its rounded corners and shadow.
/// </remarks>
public sealed class BugReportPanel
{
    /// <summary>The card's width; the window adds room for its shadow all round.</summary>
    private const double CardWidth = 640;

    /// <summary>See-through room around the card for its shadow (device-independent pixels).</summary>
    private const double ShadowSpace = 16;

    private readonly BugReportFormViewModel _form;
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly Window _window;

    /// <param name="openBug">"Open the bug": shows it on the Bug reports page (the window closes too).</param>
    public BugReportPanel(BugReportFormViewModel form, IWindowService windows, IMonitorService monitors, Action<string> openBug)
    {
        HelmAvalonia.EnsureStarted();
        _form = form;
        _windows = windows;
        _monitors = monitors;
        _window = new Window
        {
            Title = "Report a bug",
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
        };
        _window.Content = new Border
        {
            Classes = { "sheet" },
            Margin = new Thickness(ShadowSpace),
            // As the Gallery shows the form: a card of 16, its backdrop clipped to the corners.
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            Child = new BugReportFormView(form, new FormHost(this, openBug)),
        };
        _window.Closed += (_, _) =>
        {
            _form.Close();
            Closed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised once the window is gone, however it was closed.</summary>
    public event EventHandler? Closed;

    public bool IsVisible => _window.IsVisible;

    /// <summary>Shows the window on the monitor under the mouse and gives it the keyboard (the text box has the cursor).</summary>
    public void Show()
    {
        // Placed off-screen first, so it never flashes at the old spot before moving.
        _window.Position = new Avalonia.PixelPoint(-32000, -32000);
        _window.Show();
        Place();
        // Laid out at the monitor's DPI now: centre it again with its real size.
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
        Activate();
    }

    /// <summary>Brings the window to the front, even when another app has the foreground.</summary>
    public void Activate()
    {
        _window.Activate();
        if (_window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != 0) _windows.Activate(hwnd);
    }

    /// <summary>Closes the window (the form thaws the black box).</summary>
    public void Close() => _window.Close();

    /// <summary>A fifth down the monitor under the mouse, centred, like Quick Capture.</summary>
    private void Place()
    {
        if (_window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;
        if (_monitors.FromPoint(_windows.GetCursorPosition()) is not { } monitor) return;
        var scale = monitor.Dpi / 96.0;
        var work = monitor.WorkArea;
        // Never taller than the monitor: a long form scrolls inside.
        _window.MaxHeight = Math.Max(300, work.Height / scale - 40);
        var width = (int)Math.Round(_window.Bounds.Width * scale);
        var height = (int)Math.Round(_window.Bounds.Height * scale);
        var margin = (int)Math.Round(ShadowSpace * scale);
        var x = work.Left + Math.Max(0, (work.Width - width) / 2);
        var y = work.Top + Math.Max(0, Math.Min(work.Height / 5 - margin, work.Height - height));
        _windows.SetWindowPosition(hwnd, new Helm.Core.Geometry.PixelPoint(x, y));
    }

    /// <summary>The header was pressed: the title-bar-less window moves with the mouse (the system's own move loop).</summary>
    private void StartMove()
    {
        if (_window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;
        ReleaseCapture();
        SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    private sealed class FormHost(BugReportPanel panel, Action<string> openBug) : PcBugPictureSource, IBugReportFormHost
    {
        public void Close() => panel.Close();

        public void OpenBug(string id) => openBug(id);

        public bool HasKeyboard => true;

        public void DragWindow() => panel.StartMove();
    }

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const nint HTCAPTION = 2;

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessageW(IntPtr hwnd, uint msg, nint wParam, nint lParam);
}
