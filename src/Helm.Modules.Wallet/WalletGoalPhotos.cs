using Avalonia.Platform.Storage;
using Helm.Ui.Windows;
using SkiaSharp;

namespace Helm.Modules.Wallet;

/// <summary>A savings goal's photo on Windows: a picture file, made small here (SkiaSharp decodes it; no camera).</summary>
internal sealed class WalletGoalPhotos : IGoalPhotos
{
    /// <summary>The longer side of a goal's photo, in pixels: sharp on the widget and the page, small to sync.</summary>
    public const int MaxSide = 512;

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"];

    private static readonly FilePickerFileType Pictures = new("Pictures") { Patterns = Extensions.Select(x => "*" + x).ToList() };

    public bool CanTakePhoto => false;

    public async Task<byte[]?> PickAsync(CancellationToken ct = default)
    {
        var files = await DesktopWindows.PickFilesAsync("Photo of what you are saving for", [Pictures]);
        if (files.Count == 0) return null;
        await using var stream = File.OpenRead(files[0]);
        return Shrink(stream);
    }

    public Task<byte[]?> TakeAsync(CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    /// <summary>At most <see cref="MaxSide"/> on the longer side, on white (JPEG has no transparency), quality 85.</summary>
    /// <exception cref="NotSupportedException">The file is not a picture SkiaSharp can read.</exception>
    internal static byte[] Shrink(Stream stream) => JpegShrink(stream, MaxSide);

    /// <summary>Decodes a picture, scales it to at most <paramref name="maxSide"/> on its longer side, flattens it on white, JPEG 85.</summary>
    internal static byte[] JpegShrink(Stream stream, int maxSide)
    {
        using var bitmap = SKBitmap.Decode(stream) ?? throw new NotSupportedException("It is not a picture Windows can read.");
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new NotSupportedException("The picture is too large.");
        surface.Canvas.Clear(SKColors.White);
        using (var image = SKImage.FromBitmap(bitmap))
            surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var picture = surface.Snapshot();
        using var data = picture.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}
