using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helm.Modules.NovelReader;

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
        return Shrink(stream);
    }

    /// <summary>A picture downloaded from the web (a novel's cover on its site), made small the same way.</summary>
    /// <exception cref="NotSupportedException">Not a picture Windows can read.</exception>
    public static byte[] Shrink(byte[] picture)
    {
        using var stream = new MemoryStream(picture);
        return Shrink(stream);
    }

    /// <remarks>Draws with WPF: call it on the UI thread.</remarks>
    private static byte[] Shrink(Stream stream)
    {
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
