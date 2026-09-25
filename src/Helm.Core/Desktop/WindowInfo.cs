using Helm.Core.Geometry;

namespace Helm.Core.Desktop;

/// <summary>Snapshot of a top-level window. <see cref="Bounds"/> are DWM extended frame bounds (the visible frame).</summary>
public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ClassName,
    int ProcessId,
    string ProcessName,
    PixelRect Bounds,
    bool IsMaximized,
    bool IsMinimized,
    bool IsTopmost,
    bool IsCloaked)
{
    public string HandleHex => $"0x{Handle:X}";
}

/// <summary>A physical display. <see cref="Id"/> is stable across reboots (device name + resolution).</summary>
public sealed record MonitorInfo(
    nint Handle,
    string Id,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint Dpi,
    bool IsPrimary)
{
    public double Scale => Dpi / 96.0;

    public string DisplayName => $"{DeviceName.Replace(@"\\.\", string.Empty)} ({Bounds.Width}×{Bounds.Height}{(IsPrimary ? ", primary" : string.Empty)})";

    public static string MakeId(string deviceName, int width, int height) =>
        $"{deviceName.Replace(@"\\.\", string.Empty)}_{width}x{height}";
}
