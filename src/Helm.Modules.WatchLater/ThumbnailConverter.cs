using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Helm.Modules.WatchLater;

/// <summary>
/// A cached thumbnail file → an image decoded at card size. The file is read into memory (it is not kept open, so the
/// cache can be cleared) and a broken file shows the placeholder instead of an error.
/// </summary>
public sealed class ThumbnailConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 480;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            lock (Cache)
            {
                if (Cache.Count > 500) Cache.Clear();
                Cache[path] = image;
            }
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
