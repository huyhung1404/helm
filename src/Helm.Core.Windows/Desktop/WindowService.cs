using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Helm.Core.Geometry;
using Helm.Core.Hooks;
using Helm.Core.Interop;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Helm.Core.Desktop;

public interface IWindowService
{
    /// <summary>Real top-level app windows (visible, titled, not tool windows, not cloaked), in z-order.</summary>
    IReadOnlyList<WindowInfo> GetAppWindows();

    WindowInfo? GetInfo(nint hwnd);

    /// <summary>Visible, titled, top-level, not a tool window, not cloaked, not a shell surface.</summary>
    bool IsAppWindow(nint hwnd);

    /// <summary>True for windows Zones/Always On Top should act on (app windows, no owner, not shell surfaces).</summary>
    bool IsManageable(nint hwnd);

    nint GetForegroundWindow();

    string GetProcessName(nint hwnd);

    /// <summary>Full path of the window's program (for its icon); empty when Windows does not say.</summary>
    string GetProcessPath(nint hwnd);

    string GetTitle(nint hwnd);

    /// <summary>Visible frame bounds (excludes the invisible resize border). Falls back to GetWindowRect.</summary>
    PixelRect? GetFrameBounds(nint hwnd);

    /// <summary>Moves/resizes so the <em>visible</em> frame matches <paramref name="frame"/>. Restores maximized/minimized windows first.</summary>
    bool MoveWindow(nint hwnd, PixelRect frame, bool activate = false);

    /// <summary>Raw SetWindowPos in physical pixels (no border compensation) — for Helm's own borderless windows.</summary>
    bool SetWindowBounds(nint hwnd, PixelRect bounds);

    /// <summary>Moves one of Helm's own windows so its top-left corner is at a physical-pixel point; keeps its size.</summary>
    bool SetWindowPosition(nint hwnd, PixelPoint topLeft);

    bool SetTopmost(nint hwnd, bool topmost);

    /// <summary>
    /// Keeps one of Helm's own windows out of screenshots, screen recording and screen sharing (it shows as black or
    /// not at all). Returns false where Windows does not support it (before Windows 10 2004).
    /// </summary>
    bool SetExcludedFromCapture(nint hwnd, bool excluded);

    /// <summary>
    /// Turns one of Helm's own windows into a passive overlay: clicks go through it to the window below, it never
    /// takes the keyboard focus and it stays out of Alt+Tab. For hints shown over another app while you type.
    /// </summary>
    bool MakePassiveOverlay(nint hwnd);

    /// <summary>
    /// Turns one of Helm's own windows into a card you can click without it taking the keyboard focus from the app you
    /// are in, kept out of Alt+Tab. For small cards that pop up now and then.
    /// </summary>
    bool MakeNonActivating(nint hwnd) => false;

    /// <summary>
    /// Lets a window made with <see cref="MakeNonActivating"/> take the keyboard for a moment (a box to type in on the
    /// card), or makes it non-activating again.
    /// </summary>
    bool AllowActivation(nint hwnd, bool allow) => false;

    /// <summary>Restores (if minimized) and brings a window to the foreground.</summary>
    bool Activate(nint hwnd);

    /// <summary>Minimizes a window to the taskbar; Windows activates the next window in line.</summary>
    bool Minimize(nint hwnd);

    /// <summary>Restores a minimized window to where it was, without taking the focus from the window in front.</summary>
    bool RestoreWithoutActivating(nint hwnd);

    /// <summary>
    /// Brings the window to the foreground even when Windows' foreground lock would refuse a plain
    /// <see cref="Activate"/> (Helm in the tray, another app in front). Returns true only when the window really is the
    /// foreground window afterwards, so a caller can refuse to send keys anywhere else.
    /// </summary>
    bool ForceActivate(nint hwnd);

    bool IsTopmost(nint hwnd);

    bool IsWindow(nint hwnd);

    bool IsVisible(nint hwnd);

    bool IsMinimized(nint hwnd);

    bool IsMaximized(nint hwnd);

    bool IsCloaked(nint hwnd);

    PixelPoint GetCursorPosition();

    bool IsKeyDown(int virtualKey);

    /// <summary>True while a full-screen Direct3D app (game) or presentation is running.</summary>
    bool IsFullScreenGameRunning();

    /// <summary>
    /// True while anything full-screen is in front (a game, a presentation, a video, an app in F11) or nobody can see the
    /// screen (locked, screen saver): a time not to pop anything up.
    /// </summary>
    bool IsBusyFullScreen() => IsFullScreenGameRunning();
}

public sealed class WindowService(ILogger<WindowService> logger) : IWindowService
{
    private static readonly HashSet<string> s_shellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "NotifyIconOverflowWindow", "TaskListThumbnailWnd", "ForegroundStaging", "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly ConcurrentDictionary<int, (string Name, long Ticks)> _processNames = new();
    private readonly int _ownPid = Environment.ProcessId;

    public IReadOnlyList<WindowInfo> GetAppWindows()
    {
        var handles = new List<nint>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            handles.Add(hwnd);
            return true;
        }, default);

        var result = new List<WindowInfo>();
        foreach (var h in handles)
        {
            if (!IsAppWindow(h)) continue;
            if (GetInfo(h) is { } info) result.Add(info);
        }
        return result;
    }

    public WindowInfo? GetInfo(nint hwnd)
    {
        var h = hwnd.ToHwnd();
        if (!PInvoke.IsWindow(h)) return null;
        var bounds = GetFrameBounds(hwnd) ?? default;
        return new WindowInfo(
            hwnd,
            GetTitle(hwnd),
            GetClassName(hwnd),
            GetProcessId(hwnd),
            GetProcessName(hwnd),
            bounds,
            PInvoke.IsZoomed(h),
            PInvoke.IsIconic(h),
            IsTopmost(hwnd),
            IsCloaked(hwnd));
    }

    public bool IsManageable(nint hwnd)
    {
        if (!IsAppWindow(hwnd)) return false;
        var h = hwnd.ToHwnd();
        if (!PInvoke.GetWindow(h, GET_WINDOW_CMD.GW_OWNER).IsNull) return false; // dialogs / owned popups
        var style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        if (style.HasFlag(WINDOW_STYLE.WS_CHILD)) return false;
        var isPopup = style.HasFlag(WINDOW_STYLE.WS_POPUP);
        var hasCaptionOrFrame = style.HasFlag(WINDOW_STYLE.WS_THICKFRAME) || (style & WINDOW_STYLE.WS_CAPTION) == WINDOW_STYLE.WS_CAPTION;
        if (isPopup && !hasCaptionOrFrame) return false; // splash screens, menus, tooltips
        return GetProcessId(hwnd) != _ownPid;
    }

    public nint GetForegroundWindow() => PInvoke.GetForegroundWindow();

    public string GetTitle(nint hwnd)
    {
        var h = hwnd.ToHwnd();
        var length = PInvoke.GetWindowTextLength(h);
        if (length <= 0) return string.Empty;
        Span<char> buffer = length < 512 ? stackalloc char[length + 1] : new char[length + 1];
        var copied = PInvoke.GetWindowText(h, buffer);
        return new string(buffer[..copied]);
    }

    public string GetProcessName(nint hwnd)
    {
        var pid = GetProcessId(hwnd);
        if (pid == 0) return string.Empty;
        var now = Environment.TickCount64;
        if (_processNames.TryGetValue(pid, out var cached) && now - cached.Ticks < 30_000) return cached.Name;

        var name = Path.GetFileName(QueryProcessPath(pid));
        _processNames[pid] = (name, now);
        if (_processNames.Count > 512)
        {
            foreach (var stale in _processNames.Where(kv => now - kv.Value.Ticks > 30_000).Select(kv => kv.Key).ToList())
                _processNames.TryRemove(stale, out _);
        }
        return name;
    }

    public PixelRect? GetFrameBounds(nint hwnd)
    {
        var h = hwnd.ToHwnd();
        unsafe
        {
            RECT frame;
            var hr = PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT));
            if (hr.Succeeded && frame.right > frame.left) return frame.ToPixelRect();
        }
        return PInvoke.GetWindowRect(h, out var rect) ? rect.ToPixelRect() : null;
    }

    public bool MoveWindow(nint hwnd, PixelRect frame, bool activate = false)
    {
        var h = hwnd.ToHwnd();
        if (!PInvoke.IsWindow(h)) return false;

        if (PInvoke.IsZoomed(h) || PInvoke.IsIconic(h))
            PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);

        var flags = SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        if (!activate) flags |= SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;

        // The invisible resize border differs per DPI, so compute it, move, and repeat once if a DPI change
        // (moving to another monitor) altered the border or the app adjusted its size.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!PInvoke.GetWindowRect(h, out var windowRect)) return false;
            var visible = GetFrameBounds(hwnd) ?? windowRect.ToPixelRect();
            var left = visible.Left - windowRect.left;
            var top = visible.Top - windowRect.top;
            var right = windowRect.right - visible.Right;
            var bottom = windowRect.bottom - visible.Bottom;

            var target = new PixelRect(frame.Left - left, frame.Top - top, frame.Right + right, frame.Bottom + bottom);
            if (!PInvoke.SetWindowPos(h, default, target.Left, target.Top, target.Width, target.Height, flags))
            {
                logger.LogDebug("SetWindowPos failed for 0x{Hwnd:X}: {Error}", hwnd, Marshal.GetLastWin32Error());
                return false;
            }

            var after = GetFrameBounds(hwnd);
            if (after is { } a && Math.Abs(a.Left - frame.Left) <= 1 && Math.Abs(a.Top - frame.Top) <= 1
                && Math.Abs(a.Width - frame.Width) <= 1 && Math.Abs(a.Height - frame.Height) <= 1)
                break;
        }

        if (activate) PInvoke.SetForegroundWindow(h);
        return true;
    }

    public bool SetWindowBounds(nint hwnd, PixelRect bounds) =>
        PInvoke.SetWindowPos(hwnd.ToHwnd(), default, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

    public bool SetWindowPosition(nint hwnd, PixelPoint topLeft) =>
        PInvoke.SetWindowPos(hwnd.ToHwnd(), default, topLeft.X, topLeft.Y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

    public bool Activate(nint hwnd)
    {
        var h = hwnd.ToHwnd();
        if (!PInvoke.IsWindow(h)) return false;
        if (PInvoke.IsIconic(h)) PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);
        return PInvoke.SetForegroundWindow(h);
    }

    public bool Minimize(nint hwnd)
    {
        var h = hwnd.ToHwnd();
        if (!PInvoke.IsWindow(h)) return false;
        if (!PInvoke.IsIconic(h)) PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_MINIMIZE);
        return true;
    }

    public bool RestoreWithoutActivating(nint hwnd)
    {
        var h = hwnd.ToHwnd();
        if (!PInvoke.IsWindow(h)) return false;
        if (PInvoke.IsIconic(h)) PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        return true;
    }

    public bool ForceActivate(nint hwnd)
    {
        var h = hwnd.ToHwnd();
        if (!PInvoke.IsWindow(h)) return false;
        if (PInvoke.IsIconic(h)) PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);
        if (PInvoke.GetForegroundWindow() == h) return true;

        // The process that sent the last input may take the foreground: a harmless key makes that Helm.
        InputSimulator.SendDummyKey();
        if (PInvoke.SetForegroundWindow(h) && WaitForeground(h)) return true;

        // Share the foreground thread's input state for a moment, then ask again.
        var foreground = PInvoke.GetForegroundWindow();
        unsafe
        {
            var foregroundThread = PInvoke.GetWindowThreadProcessId(foreground, null);
            var ownThread = PInvoke.GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != ownThread && PInvoke.AttachThreadInput(ownThread, foregroundThread, true);
            try
            {
                PInvoke.BringWindowToTop(h);
                PInvoke.SetForegroundWindow(h);
            }
            finally
            {
                if (attached) PInvoke.AttachThreadInput(ownThread, foregroundThread, false);
            }
        }
        if (WaitForeground(h)) return true;

        // Last resort: minimise and restore, which activates the window.
        PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_MINIMIZE);
        PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);
        return WaitForeground(h);
    }

    private static bool WaitForeground(HWND h)
    {
        for (var i = 0; i < 10; i++)
        {
            if (PInvoke.GetForegroundWindow() == h) return true;
            Thread.Sleep(30);
        }
        return false;
    }

    public bool SetTopmost(nint hwnd, bool topmost)
    {
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        return PInvoke.SetWindowPos(hwnd.ToHwnd(), topmost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
    }

    public bool SetExcludedFromCapture(nint hwnd, bool excluded) =>
        PInvoke.SetWindowDisplayAffinity(hwnd.ToHwnd(), excluded ? WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE : WINDOW_DISPLAY_AFFINITY.WDA_NONE);

    public bool MakePassiveOverlay(nint hwnd)
    {
        var h = hwnd.ToHwnd();
        if (!PInvoke.IsWindow(h)) return false;
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex |= WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)ex);
        return true;
    }

    public bool MakeNonActivating(nint hwnd)
    {
        var h = hwnd.ToHwnd();
        if (!PInvoke.IsWindow(h)) return false;
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex |= WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)ex);
        return true;
    }

    public bool AllowActivation(nint hwnd, bool allow)
    {
        var h = hwnd.ToHwnd();
        if (!PInvoke.IsWindow(h)) return false;
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex = allow ? ex & ~WINDOW_EX_STYLE.WS_EX_NOACTIVATE : ex | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        PInvoke.SetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)ex);
        return true;
    }

    public bool IsTopmost(nint hwnd)
    {
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd.ToHwnd(), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        return ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST);
    }

    public bool IsWindow(nint hwnd) => hwnd != 0 && PInvoke.IsWindow(hwnd.ToHwnd());

    public bool IsVisible(nint hwnd) => PInvoke.IsWindowVisible(hwnd.ToHwnd());

    public bool IsMinimized(nint hwnd) => PInvoke.IsIconic(hwnd.ToHwnd());

    public bool IsMaximized(nint hwnd) => PInvoke.IsZoomed(hwnd.ToHwnd());

    public bool IsCloaked(nint hwnd)
    {
        unsafe
        {
            int cloaked;
            var hr = PInvoke.DwmGetWindowAttribute(hwnd.ToHwnd(), DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
            return hr.Succeeded && cloaked != 0;
        }
    }

    public PixelPoint GetCursorPosition() =>
        PInvoke.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;

    public bool IsKeyDown(int virtualKey) => (PInvoke.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public bool IsFullScreenGameRunning()
    {
        if (PInvoke.SHQueryUserNotificationState(out var state).Failed) return false;
        return state is QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
    }

    public bool IsBusyFullScreen()
    {
        if (PInvoke.SHQueryUserNotificationState(out var state).Failed) return false;
        return state is QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE
            or QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY or QUERY_USER_NOTIFICATION_STATE.QUNS_NOT_PRESENT;
    }

    public bool IsAppWindow(nint hwnd)
    {
        var h = hwnd.ToHwnd();
        if (!PInvoke.IsWindowVisible(h)) return false;
        if (PInvoke.GetAncestor(h, GET_ANCESTOR_FLAGS.GA_ROOT) != h) return false;
        if (h == PInvoke.GetShellWindow() || h == PInvoke.GetDesktopWindow()) return false;
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) || ex.HasFlag(WINDOW_EX_STYLE.WS_EX_NOACTIVATE)) return false;
        if (PInvoke.GetWindowTextLength(h) == 0) return false;
        if (IsCloaked(hwnd)) return false;
        return !s_shellClasses.Contains(GetClassName(hwnd));
    }

    private static string GetClassName(nint hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        var length = PInvoke.GetClassName(hwnd.ToHwnd(), buffer);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }

    private static int GetProcessId(nint hwnd)
    {
        unsafe
        {
            uint pid;
            PInvoke.GetWindowThreadProcessId(hwnd.ToHwnd(), &pid);
            return (int)pid;
        }
    }

    public string GetProcessPath(nint hwnd)
    {
        var pid = GetProcessId(hwnd);
        return pid == 0 ? string.Empty : QueryProcessPath(pid);
    }

    private static string QueryProcessPath(int pid)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsNull) return string.Empty;
        try
        {
            Span<char> buffer = stackalloc char[1024];
            var size = (uint)buffer.Length;
            if (!PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size)) return string.Empty;
            return new string(buffer[..(int)size]);
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }
}
