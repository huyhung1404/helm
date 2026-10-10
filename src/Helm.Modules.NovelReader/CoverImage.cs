using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helm.Modules.NovelReader;

/// <summary>A novel's cover (JPEG bytes) as a frozen image, decoded once per picture.</summary>
public sealed class CoverImage : IValueConverter
{
    private static readonly ConditionalWeakTable<byte[], ImageSource> Decoded = new();

    public static CoverImage Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        return Decoded.GetValue(bytes, Decode);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    private static ImageSource Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 240;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
        {
            return new DrawingImage();
        }
    }
}

/// <summary>Turns a picture the user picked into a small cover: at most 600 px on the long side, JPEG quality 85.</summary>
public static class CoverPicture
{
    public const int MaxSide = 600;

    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"];

    public static bool IsPicture(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <exception cref="NotSupportedException">Not a picture Windows can read.</exception>
    public static byte[] Shrink(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        // JPEG has no transparency: put the picture on white first.
        if (frame.Format != PixelFormats.Bgr24 && frame.Format != PixelFormats.Bgr32)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var area = new System.Windows.Rect(0, 0, frame.PixelWidth, frame.PixelHeight);
                context.DrawRectangle(Brushes.White, null, area);
                context.DrawImage(frame, area);
            }
            var flat = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            flat.Render(visual);
            frame = flat;
        }
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
