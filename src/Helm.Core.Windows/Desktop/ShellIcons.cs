using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.UI.Shell;
using Windows.Win32.Storage.FileSystem;

namespace Helm.Core.Desktop;

/// <summary>The icon Explorer shows for a file (an app's shortcut shows the app's icon), as a frozen WPF image.</summary>
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file's large icon (32 px), cached per path; null when Windows has none for it.</summary>
    public static ImageSource? ForFile(string path) => Cache.GetOrAdd(path, Load);

    private static unsafe ImageSource? Load(string path)
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
                var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally
            {
                PInvoke.DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return null;
        }
    }
}
