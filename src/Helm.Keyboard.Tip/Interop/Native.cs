using System.Runtime.InteropServices;

namespace Helm.Typing.Tip.Interop;

internal static partial class Native
{
    public const int VK_BACK = 0x08;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_CAPITAL = 0x14;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_RMENU = 0xA5;

    public const uint CLSCTX_INPROC_SERVER = 0x1;
    public const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;
    public const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;

    public const uint SYNCHRONIZE = 0x00100000;
    public const int ERROR_ACCESS_DENIED = 5;
    public const uint WAIT_TIMEOUT = 0x102;

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int virtualKey);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, int inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleHandleEx(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW")]
    public static unsafe partial uint GetModuleFileName(nint module, char* fileName, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "OutputDebugStringW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial void OutputDebugString(string message);

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public const int TRANSPARENT = 1;
    public const uint DT_CENTER = 0x1;
    public const uint DT_VCENTER = 0x4;
    public const uint DT_SINGLELINE = 0x20;
    public const uint ANTIALIASED_QUALITY = 4;

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    public static unsafe partial nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    public static unsafe partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, void* bits);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint dc, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetTextColor(nint dc, uint color);

    [LibraryImport("gdi32.dll")]
    public static partial int SetBkMode(nint dc, int mode);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW")]
    public static unsafe partial int DrawText(nint dc, char* text, int length, Rect* rect, uint format);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint dc);

    [LibraryImport("user32.dll")]
    public static unsafe partial nint CreateIconIndirect(IconInfo* info);

    public static bool IsDown(int virtualKey) => GetKeyState(virtualKey) < 0;

    public static bool IsToggled(int virtualKey) => (GetKeyState(virtualKey) & 1) != 0;
}
