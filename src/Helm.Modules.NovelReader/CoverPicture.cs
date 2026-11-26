using SkiaSharp;

namespace Helm.Modules.NovelReader;

/// <summary>Turns a picture the user picked into a small cover: at most 600 px on the long side, JPEG quality 85.</summary>
/// <remarks>SkiaSharp (what Avalonia draws with) does the work: any thread.</remarks>
public static class CoverPicture
{
    public const int MaxSide = 600;

    /// <summary>What SkiaSharp reads (no TIFF).</summary>
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"];

    public static bool IsPicture(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <exception cref="NotSupportedException">Not a picture SkiaSharp can read.</exception>
    public static byte[] Shrink(string path)
    {
        using var stream = File.OpenRead(path);
        return Shrink(stream);
    }

    /// <summary>A picture downloaded from the web (a novel's cover on its site), made small the same way.</summary>
    /// <exception cref="NotSupportedException">Not a picture SkiaSharp can read.</exception>
    public static byte[] Shrink(byte[] picture)
    {
        using var stream = new MemoryStream(picture);
        return Shrink(stream);
    }

    private static byte[] Shrink(Stream stream)
    {
        using var bitmap = SKBitmap.Decode(stream) ?? throw new NotSupportedException("It is not a picture Windows can read.");
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new NotSupportedException("The picture is too large.");
        // JPEG has no transparency: put the picture on white first.
        surface.Canvas.Clear(SKColors.White);
        using (var image = SKImage.FromBitmap(bitmap))
            surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var flat = surface.Snapshot();
        using var data = flat.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}
