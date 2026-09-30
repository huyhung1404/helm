using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Helm.Modules.WatchLater;

/// <summary>
/// A cached thumbnail file → a bitmap decoded at card size (phones have little memory to spare). A broken file shows
/// the placeholder instead of an error.
/// </summary>
public sealed class ThumbnailConverter : IValueConverter
{
    public static ThumbnailConverter Instance { get; } = new();

    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(stream, 480);
            lock (Cache)
            {
                if (Cache.Count > 300) Cache.Clear();
                Cache[path] = bitmap;
            }
            return bitmap;
        }
        catch (Exception)
        {
            // Missing, unreadable or not an image (the decoder throws its own exception types).
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
