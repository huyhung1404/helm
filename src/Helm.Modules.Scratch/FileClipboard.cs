using System.Runtime.InteropServices;
using System.Text;

namespace Helm.Modules.Scratch;

/// <summary>
/// Puts files on the Windows clipboard (CF_HDROP, as Explorer's Copy does), optionally with a picture (CF_DIB) for apps
/// that take only pictures. Win32 only: works on any thread and with no Helm window open.
/// </summary>
internal static class FileClipboard
{
    private const uint CfHdrop = 15;
    private const uint CfDib = 8;
    private const uint GmemMoveable = 0x0002;
    private const int DropFilesSize = 20;

    /// <summary>A picture as 32-bit BGRA pixels, top row first.</summary>
    public sealed record Picture(int Width, int Height, byte[] Bgra);

    /// <summary>Replaces the clipboard with <paramref name="paths"/> (and <paramref name="picture"/>).</summary>
    /// <exception cref="InvalidOperationException">Another app keeps the clipboard open.</exception>
    public static void SetFiles(IReadOnlyList<string> paths, Picture? picture, nint owner = 0)
    {
        var drop = DropFiles(paths);
        var dib = picture is null ? null : Dib(picture);
        for (var attempt = 0; ; attempt++)
        {
            if (OpenClipboard(owner)) break;
            // Another app has the clipboard open for a moment.
            if (attempt >= 9) throw new InvalidOperationException("The clipboard is busy. Try again in a moment.");
            Thread.Sleep(50);
        }
        try
        {
            if (!EmptyClipboard() || !SetBlock(CfHdrop, drop)) throw new InvalidOperationException("Windows did not take the files onto the clipboard.");
            if (dib is not null) SetBlock(CfDib, dib);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>DROPFILES (offset of the list, a point, two flags: wide characters) then each path, ending with an empty one.</summary>
    private static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        var list = Encoding.Unicode.GetBytes(string.Concat(paths.Select(p => p + "\0")) + "\0");
        var bytes = new byte[DropFilesSize + list.Length];
        BitConverter.GetBytes(DropFilesSize).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 16);
        list.CopyTo(bytes, DropFilesSize);
        return bytes;
    }

    /// <summary>BITMAPINFOHEADER and the pixels, bottom row first, 32 bits per pixel.</summary>
    private static byte[] Dib(Picture picture)
    {
        int width = picture.Width, height = picture.Height, stride = width * 4;
        var bytes = new byte[40 + stride * height];
        BitConverter.GetBytes(40).CopyTo(bytes, 0);
        BitConverter.GetBytes(width).CopyTo(bytes, 4);
        BitConverter.GetBytes(height).CopyTo(bytes, 8);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 12);
        BitConverter.GetBytes((short)32).CopyTo(bytes, 14);
        BitConverter.GetBytes(stride * height).CopyTo(bytes, 20);
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(picture.Bgra, row * stride, bytes, 40 + (height - 1 - row) * stride, stride);
        return bytes;
    }

    private static bool SetBlock(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
        if (handle == 0) return false;
        var pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            GlobalFree(handle);
            return false;
        }
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        GlobalUnlock(handle);
        // The clipboard owns the memory once SetClipboardData succeeds.
        if (SetClipboardData(format, handle) != 0) return true;
        GlobalFree(handle);
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);
}
