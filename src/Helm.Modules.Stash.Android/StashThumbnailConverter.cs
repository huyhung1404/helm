using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Helm.Modules.Stash;

/// <summary>
/// A thumbnail's JPEG bytes (from the record) → a bitmap. Decoded once per byte array; a broken one shows the kind's
/// icon instead of an error.
/// </summary>
public sealed class StashThumbnailConverter : IValueConverter
{
    public static StashThumbnailConverter Instance { get; } = new();

    private static readonly ConditionalWeakTable<byte[], Bitmap> Cache = [];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        if (Cache.TryGetValue(bytes, out var cached)) return cached;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(stream);
            Cache.AddOrUpdate(bytes, bitmap);
            return bitmap;
        }
        catch (Exception)
        {
            // Not an image the decoder knows (it throws its own exception types).
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
