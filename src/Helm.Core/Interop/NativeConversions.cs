using Helm.Core.Geometry;
using Windows.Win32.Foundation;

namespace Helm.Core.Interop;

internal static class NativeConversions
{
    public static PixelRect ToPixelRect(this RECT r) => new(r.left, r.top, r.right, r.bottom);

    public static RECT ToRect(this PixelRect r) => new() { left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom };

    public static HWND ToHwnd(this nint handle) => new(handle);

    public static COLORREF ToColorRef(this RgbColor c) => new((uint)(c.R | (c.G << 8) | (c.B << 16)));
}
