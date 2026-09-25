using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Helm.Core.Geometry;
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

    /// <summary>True for windows Zones/Always On Top should act on (app windows, no owner, not shell surfaces).</summary>
    bool IsManageable(nint hwnd);

    nint GetForegroundWindow();

    string GetProcessName(nint hwnd);

    string GetTitle(nint hwnd);

    /// <summary>Visible frame bounds (excludes the invisible resize border). Falls back to GetWindowRect.</summary>
    PixelRect? GetFrameBounds(nint hwnd);

    /// <summary>Moves/resizes so the <em>visible</em> frame matches <paramref name="frame"/>. Restores maximized/minimized windows first.</summary>
    bool MoveWindow(nint hwnd, PixelRect frame, bool activate = false);

    bool SetTopmost(nint hwnd, bool topmost);

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

        var name = QueryProcessName(pid);
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

    public bool SetTopmost(nint hwnd, bool topmost)
    {
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        return PInvoke.SetWindowPos(hwnd.ToHwnd(), topmost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
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

    private bool IsAppWindow(nint hwnd)
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

    private static string QueryProcessName(int pid)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsNull) return string.Empty;
        try
        {
            Span<char> buffer = stackalloc char[1024];
            var size = (uint)buffer.Length;
            if (!PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size)) return string.Empty;
            return Path.GetFileName(new string(buffer[..(int)size]));
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }
}
