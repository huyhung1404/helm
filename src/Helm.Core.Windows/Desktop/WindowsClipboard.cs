using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Helm.Core.Desktop;

/// <summary>
/// The Windows clipboard without any UI framework: text, the files copied in Explorer and a copied picture. Call from
/// any thread (it opens the clipboard briefly, retrying while another app holds it). Failures read as "nothing there"
/// and writes return false.
/// </summary>
public static class WindowsClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint CfHdrop = 15;
    private const uint CfDib = 8;
    private const uint GmemMoveable = 0x0002;

    /// <summary>Puts <paramref name="text"/> on the clipboard. <paramref name="excludeFromHistory"/>: Windows' clipboard
    /// history and cloud clipboard do not keep it (a password).</summary>
    public static bool SetText(string text, bool excludeFromHistory = false)
    {
        return WithClipboard(() =>
        {
            if (!EmptyClipboard()) return false;
            if (!SetBlock(CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0"))) return false;
            if (excludeFromHistory)
            {
                // Read by Windows' clipboard history and cloud clipboard: a DWORD 0 means "do not keep this".
                SetBlock(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), [0, 0, 0, 0]);
                SetBlock(RegisterClipboardFormat("CanIncludeInClipboardHistory"), [0, 0, 0, 0]);
                SetBlock(RegisterClipboardFormat("CanUploadToCloudClipboard"), [0, 0, 0, 0]);
            }
            return true;
        });
    }

    /// <summary>The clipboard's text, or null.</summary>
    public static string? GetText() => WithClipboard(() =>
    {
        if (!IsClipboardFormatAvailable(CfUnicodeText)) return null;
        var bytes = GetBlock(CfUnicodeText);
        if (bytes is null) return null;
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    });

    public static bool ContainsText() => IsClipboardFormatAvailable(CfUnicodeText);

    /// <summary>The files (and folders) copied in Explorer; empty when there are none.</summary>
    public static IReadOnlyList<string> GetFiles() => WithClipboard<IReadOnlyList<string>>(() =>
    {
        if (!IsClipboardFormatAvailable(CfHdrop)) return [];
        var handle = GetClipboardData(CfHdrop);
        if (handle == IntPtr.Zero) return [];
        var count = DragQueryFile(handle, 0xFFFFFFFF, null, 0);
        var files = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFile(handle, i, null, 0);
            var name = new StringBuilder((int)length + 1);
            DragQueryFile(handle, i, name, length + 1);
            files.Add(name.ToString());
        }
        return files;
    }) ?? [];

    /// <summary>
    /// A copied picture as image-file bytes Avalonia (and browsers) can read: the "PNG" format when the app put one
    /// (browsers, Snipping Tool), else the bitmap (CF_DIB) as a .bmp file. Null when there is none.
    /// </summary>
    public static byte[]? GetImage() => WithClipboard(() =>
    {
        var png = RegisterClipboardFormat("PNG");
        if (IsClipboardFormatAvailable(png) && GetBlock(png) is { Length: > 0 } pngBytes) return pngBytes;
        if (!IsClipboardFormatAvailable(CfDib) || GetBlock(CfDib) is not { Length: > 40 } dib) return null;
        // A BMP file is the DIB with a 14-byte file header in front; the pixels start after the header and the palette.
        var headerSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var colorsUsed = BitConverter.ToInt32(dib, 32);
        var palette = colorsUsed != 0 ? colorsUsed * 4 : bitCount <= 8 ? (1 << bitCount) * 4 : compression == 3 && headerSize == 40 ? 12 : 0;
        using var bmp = new MemoryStream(14 + dib.Length);
        using var writer = new BinaryWriter(bmp);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(14 + dib.Length);
        writer.Write(0);
        writer.Write(14 + headerSize + palette);
        writer.Write(dib);
        return bmp.ToArray();
    });

    public static bool ContainsImage() =>
        IsClipboardFormatAvailable(CfDib) || IsClipboardFormatAvailable(RegisterClipboardFormat("PNG"));

    public static bool ContainsFiles() => IsClipboardFormatAvailable(CfHdrop);

    private static T? WithClipboard<T>(Func<T?> action)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    return action();
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(20);
        }
        return default;
    }

    private static bool WithClipboard(Func<bool> action) => WithClipboard<object>(() => action() ? true : null) is not null;

    private static byte[]? GetBlock(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = (int)GlobalSize(handle);
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool SetBlock(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) return false;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        GlobalUnlock(handle);
        // The clipboard owns the memory once SetClipboardData succeeds.
        if (SetClipboardData(format, handle) != IntPtr.Zero) return true;
        GlobalFree(handle);
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, uint length);
}
