using System.Windows.Media;
using System.Windows.Media.Imaging;
using Helm.Core.Settings;
using Helm.Modules.Stash;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public sealed class StashWindowsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_picture_gets_a_small_jpeg_thumbnail_with_its_shape_kept()
    {
        var path = Path.Combine(_dir.Path, "wide.png");
        WritePng(path, 800, 400, alpha: 255);
        var platform = new WindowsStashPlatform(new HelmPaths(_dir.Path), NullLogger<WindowsStashPlatform>.Instance);

        var thumbnail = await platform.ThumbnailAsync(WindowsStashPlatform.FromPath(path), CancellationToken.None);

        Assert.NotNull(thumbnail);
        Assert.True(thumbnail.Length <= StashItem.MaxThumbnailBytes);
        var frame = BitmapDecoder.Create(new MemoryStream(thumbnail), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(360, frame.PixelWidth);
        Assert.Equal(180, frame.PixelHeight);
        Assert.IsType<JpegBitmapDecoder>(frame.Decoder);
    }

    [Fact]
    public async Task Transparent_parts_of_a_picture_become_white_in_its_thumbnail()
    {
        var path = Path.Combine(_dir.Path, "clear.png");
        WritePng(path, 100, 100, alpha: 0);
        var platform = new WindowsStashPlatform(new HelmPaths(_dir.Path), NullLogger<WindowsStashPlatform>.Instance);

        var thumbnail = await platform.ThumbnailAsync(WindowsStashPlatform.FromPath(path), CancellationToken.None);

        var frame = new FormatConvertedBitmap(BitmapDecoder.Create(new MemoryStream(thumbnail!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0],
            PixelFormats.Bgr24, null, 0);
        var pixel = new byte[3];
        frame.CopyPixels(new System.Windows.Int32Rect(50, 50, 1, 1), pixel, 3, 0);
        Assert.All(pixel, b => Assert.True(b > 240));
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_gets_no_thumbnail()
    {
        var path = Path.Combine(_dir.Path, "notes.pdf");
        await File.WriteAllTextAsync(path, "%PDF-1.4 not really");
        var platform = new WindowsStashPlatform(new HelmPaths(_dir.Path), NullLogger<WindowsStashPlatform>.Instance);

        var source = WindowsStashPlatform.FromPath(path);

        Assert.Equal("application/pdf", source.MediaType);
        Assert.Null(await platform.ThumbnailAsync(source with { Origin = null }, CancellationToken.None));
    }

    private static void WritePng(string path, int width, int height, byte alpha)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 1] = 30;
            pixels[i + 2] = 60;
            pixels[i + 3] = alpha;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
