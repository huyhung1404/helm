using System.Runtime.InteropServices.WindowsRuntime;
using Helm.Core.Settings;
using Helm.Modules.Scratch;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Graphics.Imaging;

namespace Helm.Tests;

public sealed class ScratchWindowsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_picture_gets_a_small_jpeg_thumbnail_with_its_shape_kept()
    {
        var path = Path.Combine(_dir.Path, "wide.png");
        await WritePngAsync(path, 800, 400, alpha: 255);
        var platform = new WindowsScratchPlatform(new HelmPaths(_dir.Path), NullLogger<WindowsScratchPlatform>.Instance);

        var thumbnail = await platform.ThumbnailAsync(WindowsScratchPlatform.FromPath(path), CancellationToken.None);

        Assert.NotNull(thumbnail);
        Assert.True(thumbnail.Length <= ScratchItem.MaxThumbnailBytes);
        var decoder = await BitmapDecoder.CreateAsync(new MemoryStream(thumbnail).AsRandomAccessStream());
        Assert.Equal(360u, decoder.PixelWidth);
        Assert.Equal(180u, decoder.PixelHeight);
        Assert.Equal(BitmapDecoder.JpegDecoderId, decoder.DecoderInformation.CodecId);
    }

    [Fact]
    public async Task Transparent_parts_of_a_picture_become_white_in_its_thumbnail()
    {
        var path = Path.Combine(_dir.Path, "clear.png");
        await WritePngAsync(path, 100, 100, alpha: 0);
        var platform = new WindowsScratchPlatform(new HelmPaths(_dir.Path), NullLogger<WindowsScratchPlatform>.Instance);

        var thumbnail = await platform.ThumbnailAsync(WindowsScratchPlatform.FromPath(path), CancellationToken.None);

        var decoder = await BitmapDecoder.CreateAsync(new MemoryStream(thumbnail!).AsRandomAccessStream());
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var at = (50 * (int)decoder.PixelWidth + 50) * 4;
        Assert.All(pixels[at..(at + 3)], b => Assert.True(b > 240));
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_gets_no_thumbnail()
    {
        var path = Path.Combine(_dir.Path, "notes.pdf");
        await File.WriteAllTextAsync(path, "%PDF-1.4 not really");
        var platform = new WindowsScratchPlatform(new HelmPaths(_dir.Path), NullLogger<WindowsScratchPlatform>.Instance);

        var source = WindowsScratchPlatform.FromPath(path);

        Assert.Equal("application/pdf", source.MediaType);
        Assert.Null(await platform.ThumbnailAsync(source with { Origin = null }, CancellationToken.None));
    }

    private static async Task WritePngAsync(string path, int width, int height, byte alpha)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 1] = 30;
            pixels[i + 2] = 60;
            pixels[i + 3] = alpha;
        }
        await using var file = File.Create(path);
        using var output = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
