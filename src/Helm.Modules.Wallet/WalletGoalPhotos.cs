using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Helm.Modules.Wallet;

/// <summary>A savings goal's photo on Windows: a picture file, made small here (WPF decodes it; no camera).</summary>
internal sealed class WalletGoalPhotos : IGoalPhotos
{
    /// <summary>The longer side of a goal's photo, in pixels: sharp on the widget and the page, small to sync.</summary>
    public const int MaxSide = 512;

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"];

    public bool CanTakePhoto => false;

    public Task<byte[]?> PickAsync(CancellationToken ct = default)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Photo of what you are saving for",
            Filter = "Pictures|" + string.Join(";", Extensions.Select(x => "*" + x)),
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return Task.FromResult<byte[]?>(null);
        try
        {
            using var stream = File.OpenRead(dialog.FileName);
            // WPF imaging (RenderTargetBitmap) needs the UI thread; one picture is quick.
            return Task.FromResult<byte[]?>(Shrink(stream));
        }
        catch (Exception ex) when (ex is FileFormatException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            throw new NotSupportedException("It is not a picture Windows can read.", ex);
        }
    }

    public Task<byte[]?> TakeAsync(CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    /// <summary>At most <see cref="MaxSide"/> on the longer side, on white (JPEG has no transparency), quality 85.</summary>
    /// <remarks>Draws with WPF: call it on the UI thread.</remarks>
    internal static byte[] Shrink(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        if (frame.Format != PixelFormats.Bgr24 && frame.Format != PixelFormats.Bgr32)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var area = new Rect(0, 0, frame.PixelWidth, frame.PixelHeight);
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
