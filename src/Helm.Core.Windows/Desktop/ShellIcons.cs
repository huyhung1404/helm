using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Helm.Core.Desktop;

/// <summary>The icon Explorer shows for a file (an app's shortcut shows the app's icon), as an Avalonia bitmap.</summary>
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file's large icon (32 px), cached per path; null when Windows has none for it.</summary>
    public static Bitmap? ForFile(string path) => Cache.GetOrAdd(path, Load);

    private static unsafe Bitmap? Load(string path)
    {
        try
        {
            var info = new SHFILEINFOW();
            nuint result;
            fixed (char* p = path)
            {
                result = PInvoke.SHGetFileInfo(new Windows.Win32.Foundation.PCWSTR(p), FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, &info,
                    (uint)sizeof(SHFILEINFOW), SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_LARGEICON);
            }
            if (result == 0 || info.hIcon.IsNull) return null;
            try
            {
                return FromIcon(info.hIcon);
            }
            finally
            {
                PInvoke.DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The icon's pixels through GDI: its colour bitmap as 32-bit BGRA, top-down. An old icon without alpha (every alpha
    /// byte 0) takes its transparency from the mask instead.
    /// </summary>
    private static unsafe Bitmap? FromIcon(HICON icon)
    {
        if (!PInvoke.GetIconInfo(icon, out var iconInfo)) return null;
        try
        {
            if (iconInfo.hbmColor.IsNull) return null;
            BITMAP bitmap;
            if (PInvoke.GetObject(iconInfo.hbmColor, sizeof(BITMAP), &bitmap) == 0) return null;
            int width = bitmap.bmWidth, height = bitmap.bmHeight;
            if (width <= 0 || height <= 0) return null;

            var pixels = Pixels(iconInfo.hbmColor, width, height);
            if (pixels is null) return null;
            var hasAlpha = false;
            for (var i = 3; i < pixels.Length && !hasAlpha; i += 4) hasAlpha = pixels[i] != 0;
            if (!hasAlpha && !iconInfo.hbmMask.IsNull && Pixels(iconInfo.hbmMask, width, height) is { } mask)
            {
                // The mask is white where the icon is see-through.
                for (var i = 0; i < pixels.Length; i += 4) pixels[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
            }

            fixed (byte* data = pixels)
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, (nint)data, new PixelSize(width, height), new Vector(96, 96), width * 4);
            }
        }
        finally
        {
            if (!iconInfo.hbmColor.IsNull) PInvoke.DeleteObject(iconInfo.hbmColor);
            if (!iconInfo.hbmMask.IsNull) PInvoke.DeleteObject(iconInfo.hbmMask);
        }
    }

    /// <summary>A GDI bitmap's pixels as 32-bit BGRA rows, top-down; null when GDI cannot give them.</summary>
    private static unsafe byte[]? Pixels(HBITMAP bitmap, int width, int height)
    {
        var header = new BITMAPINFO();
        header.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        header.bmiHeader.biWidth = width;
        header.bmiHeader.biHeight = -height; // top-down
        header.bmiHeader.biPlanes = 1;
        header.bmiHeader.biBitCount = 32;
        header.bmiHeader.biCompression = 0; // BI_RGB
        var pixels = new byte[width * height * 4];
        var dc = PInvoke.CreateCompatibleDC(HDC.Null);
        try
        {
            fixed (byte* data = pixels)
            {
                return PInvoke.GetDIBits(dc, bitmap, 0, (uint)height, data, &header, DIB_USAGE.DIB_RGB_COLORS) == 0 ? null : pixels;
            }
        }
        finally
        {
            PInvoke.DeleteDC(dc);
        }
    }
}
