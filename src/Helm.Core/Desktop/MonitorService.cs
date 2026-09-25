using Helm.Core.Geometry;
using Helm.Core.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Helm.Core.Desktop;

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> GetMonitors();

    MonitorInfo? FromPoint(PixelPoint point);

    MonitorInfo? FromWindow(nint hwnd);

    MonitorInfo? FromRect(PixelRect rect);
}

public sealed class MonitorService : IMonitorService
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var handles = new List<HMONITOR>();
        unsafe
        {
            PInvoke.EnumDisplayMonitors(default, (RECT?)null, (monitor, _, _, _) =>
            {
                handles.Add(monitor);
                return true;
            }, default);
        }

        return handles
            .Select(Describe)
            .OfType<MonitorInfo>()
            .OrderBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();
    }

    public MonitorInfo? FromPoint(PixelPoint point) =>
        Describe(PInvoke.MonitorFromPoint(new System.Drawing.Point(point.X, point.Y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST));

    public MonitorInfo? FromWindow(nint hwnd) =>
        Describe(PInvoke.MonitorFromWindow(hwnd.ToHwnd(), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST));

    public MonitorInfo? FromRect(PixelRect rect)
    {
        var r = rect.ToRect();
        return Describe(PInvoke.MonitorFromRect(r, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST));
    }

    private static unsafe MonitorInfo? Describe(HMONITOR monitor)
    {
        if (monitor.IsNull) return null;
        var info = new MONITORINFOEXW();
        info.monitorInfo.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>();
        unsafe
        {
            if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info)) return null;
        }

        string device;
        unsafe
        {
            var span = info.szDevice.AsReadOnlySpan();
            var nul = span.IndexOf('\0');
            device = (nul >= 0 ? span[..nul] : span).ToString();
        }

        uint dpi = 96;
        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _).Succeeded) dpi = dpiX;

        var bounds = info.monitorInfo.rcMonitor.ToPixelRect();
        return new MonitorInfo(
            (nint)monitor.Value,
            MonitorInfo.MakeId(device, bounds.Width, bounds.Height),
            device,
            bounds,
            info.monitorInfo.rcWork.ToPixelRect(),
            dpi,
            (info.monitorInfo.dwFlags & 1) != 0); // MONITORINFOF_PRIMARY
    }
}
