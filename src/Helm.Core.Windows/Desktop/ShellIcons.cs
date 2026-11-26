using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Shell;

namespace Helm.Core.Desktop;

/// <summary>The icon Explorer shows for a file (an app's shortcut shows the app's icon), as an Avalonia bitmap.</summary>
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, Avalonia.Media.Imaging.Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file's large icon (32 px), cached per path; null when Windows has none for it.</summary>
    public static Avalonia.Media.Imaging.Bitmap? ForFile(string path) => Cache.GetOrAdd(path, Load);

    private static unsafe Avalonia.Media.Imaging.Bitmap? Load(string path)
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
                // Windows' imaging turns the icon (with its alpha) into a PNG; Avalonia reads that.
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var png = new MemoryStream();
                encoder.Save(png);
                png.Position = 0;
                return new Avalonia.Media.Imaging.Bitmap(png);
            }
            finally
            {
                PInvoke.DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or ArgumentException or IOException)
        {
            return null;
        }
    }
}
