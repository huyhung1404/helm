using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Helm.Modules.Scratch;

/// <summary>
/// A thumbnail's JPEG bytes (from the record) → an image. Decoded once per byte array; a broken one shows the kind's
/// icon instead of an error.
/// </summary>
public sealed class ScratchThumbnailConverter : IValueConverter
{
    private static readonly ConditionalWeakTable<byte[], BitmapImage> Cache = [];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        if (Cache.TryGetValue(bytes, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes, writable: false);
            image.EndInit();
            image.Freeze();
            Cache.AddOrUpdate(bytes, image);
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException
                                       or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
