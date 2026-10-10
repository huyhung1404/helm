using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Helm.Modules.NovelReader;

/// <summary>A novel's cover (JPEG bytes) as an Avalonia bitmap, decoded once per picture.</summary>
public sealed class CoverImage : IValueConverter
{
    public static CoverImage Instance { get; } = new();

    private static readonly ConditionalWeakTable<byte[], Bitmap> Decoded = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        if (Decoded.TryGetValue(bytes, out var bitmap)) return Wrap(bitmap, targetType);
        try
        {
            using var stream = new MemoryStream(bytes);
            // The library shows covers about 120 px tall: decode at that size, not 600.
            bitmap = Bitmap.DecodeToHeight(stream, 240);
            Decoded.AddOrUpdate(bytes, bitmap);
            return Wrap(bitmap, targetType);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    /// <summary>An ImageBrush for a Border's background, or the bitmap itself for an Image.</summary>
    private static object Wrap(Bitmap bitmap, Type targetType) =>
        targetType.IsAssignableFrom(typeof(Bitmap)) ? bitmap : new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
}

/// <summary>The drawn cover of a novel without a picture: its two colours (from its id) as a gradient, bottom-left to top-right.</summary>
public sealed class CoverGradient : IValueConverter
{
    public static CoverGradient Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string id) return null;
        var (start, end) = CoverArt.Colors(id);
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 0, Avalonia.RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse(start), 0), new GradientStop(Color.Parse(end), 1) },
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
