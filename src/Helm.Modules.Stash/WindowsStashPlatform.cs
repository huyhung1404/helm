using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Helm.Modules.Stash;

/// <summary>
/// The Windows side of Stash: file dialogs, thumbnails (WIC for pictures, the shell for videos), the default app for
/// opening, and the clipboard for Copy (as files, plus the picture itself for a photo).
/// </summary>
internal sealed class WindowsStashPlatform : IStashPlatform
{
    private const int ThumbnailSide = 256;

    private readonly ILogger _logger;

    public WindowsStashPlatform(HelmPaths paths, ILogger<WindowsStashPlatform> logger)
    {
        _logger = logger;
        OpenFolder = Path.Combine(paths.Root, "cache", StashIds.ModuleId, "open");
        try
        {
            // Out of Windows Search and hidden: the copies are temporary and may be private.
            var root = Directory.CreateDirectory(OpenFolder);
            root.Attributes |= FileAttributes.NotContentIndexed | FileAttributes.Hidden;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prepare the stash's open folder");
        }
    }

    public string OpenFolder { get; }

    public string SendLabel => "Copy";

    public Task<IReadOnlyList<StashSource>> PickFilesAsync(CancellationToken ct)
    {
        var dialog = new OpenFileDialog { Title = "Add to Stash", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return Task.FromResult<IReadOnlyList<StashSource>>([]);
        return Task.FromResult<IReadOnlyList<StashSource>>(dialog.FileNames.Select(FromPath).ToList());
    }

    /// <summary>A file on disk (picked, dropped or copied in Explorer).</summary>
    public static StashSource FromPath(string path) =>
        new(Path.GetFileName(path), StashFormat.MediaTypeOf(path), new FileInfo(path).Length,
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true), path);

    /// <summary>A picture from the clipboard (a screenshot), as PNG.</summary>
    public static StashSource? FromClipboardImage(DateTimeOffset now)
    {
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        var bytes = memory.ToArray();
        return new StashSource(StashFormat.NameFor("image/png", now), "image/png", bytes.Length, () => new MemoryStream(bytes, writable: false));
    }

    public Task<byte[]?> ThumbnailAsync(StashSource source, CancellationToken ct) => Task.Run(async () =>
    {
        var mediaType = StashFormat.PickMediaType(source.MediaType, source.Name);
        if (mediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            try
            {
                return PictureThumbnail(source.OpenRead);
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException
                                           or InvalidOperationException or COMException or OverflowException)
            {
                // HEIC without the codec, a damaged file: the shell may still have a thumbnail.
                _logger.LogDebug(ex, "WIC could not decode a picture for its thumbnail");
            }
        }
        return source.Origin is string path && File.Exists(path) ? await ShellThumbnailAsync(path).ConfigureAwait(false) : null;
    }, ct);

    public Task OpenAsync(StashLocalFile file, CancellationToken ct)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "No app opens {Extension} files", Path.GetExtension(file.Name));
            throw new InvalidOperationException($"Windows has no app to open {Path.GetExtension(file.Name)} files. Use Save as instead.");
        }
        return Task.CompletedTask;
    }

    public async Task<string?> SaveAsync(StashLocalFile file, CancellationToken ct)
    {
        var extension = Path.GetExtension(file.Name);
        var dialog = new SaveFileDialog
        {
            Title = "Save a copy",
            FileName = file.Name,
            Filter = extension.Length > 0 ? $"{extension.TrimStart('.').ToUpperInvariant()} file|*{extension}|All files|*.*" : "All files|*.*",
            InitialDirectory = Downloads(),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return null;
        await using (var from = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var to = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await from.CopyToAsync(to, ct).ConfigureAwait(true);
        return Path.GetDirectoryName(dialog.FileName) ?? dialog.FileName;
    }

    /// <summary>
    /// The files go on the clipboard as files (paste into Explorer, a chat or an email); a single photo also as a
    /// picture, for apps that take only pictures.
    /// </summary>
    public Task<string?> SendAsync(IReadOnlyList<StashLocalFile> files, CancellationToken ct)
    {
        SetClipboard(files);
        var what = files.Count == 1 ? $"“{files[0].Name}”" : $"{files.Count} files";
        return Task.FromResult<string?>($"Copied {what}. Paste it into a folder, a chat or an email.");
    }

    public Task CopyToClipboardAsync(StashLocalFile file, CancellationToken ct)
    {
        SetClipboard([file]);
        return Task.CompletedTask;
    }

    private void SetClipboard(IReadOnlyList<StashLocalFile> files)
    {
        var data = new DataObject();
        var list = new StringCollection();
        list.AddRange(files.Select(f => f.Path).ToArray());
        data.SetFileDropList(list);
        if (files is [{ } single] && single.MediaType.StartsWith("image/", StringComparison.Ordinal) && LoadPicture(single.Path) is { } picture)
            data.SetImage(picture);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                break;
            }
            catch (COMException) when (attempt < 4)
            {
                // Another app has the clipboard open for a moment.
                Thread.Sleep(50);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException("The clipboard is busy. Try again in a moment.", ex);
            }
        }
    }

    // ---- Thumbnails ----------------------------------------------------------------------------------------------

    /// <summary>Decoded at thumbnail size (not full size), turned upright as the camera recorded it.</summary>
    private static byte[] PictureThumbnail(Func<Stream> open)
    {
        int width, height;
        var orientation = 1;
        using (var probe = Seekable(open()))
        {
            var frame = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            try
            {
                if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort value) orientation = value;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException)
            {
                // PNG, GIF: no camera orientation.
            }
        }
        var image = new BitmapImage();
        using (var stream = Seekable(open()))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width >= height) image.DecodePixelWidth = Math.Min(ThumbnailSide, Math.Max(1, width));
            else image.DecodePixelHeight = Math.Min(ThumbnailSide, Math.Max(1, height));
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        BitmapSource upright = orientation switch
        {
            3 => new TransformedBitmap(image, new RotateTransform(180)),
            6 => new TransformedBitmap(image, new RotateTransform(90)),
            8 => new TransformedBitmap(image, new RotateTransform(270)),
            _ => image,
        };
        return Jpeg(upright);
    }

    /// <summary>What Explorer shows for the file (videos, HEIC with the extension installed); null when it has only an icon.</summary>
    private async Task<byte[]?> ShellThumbnailAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, ThumbnailSide,
                Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Type != Windows.Storage.FileProperties.ThumbnailType.Image) return null;
            using var stream = Seekable(thumbnail.AsStreamForRead());
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var scale = Math.Min(1.0, (double)ThumbnailSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource sized = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
            return Jpeg(sized);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No shell thumbnail for a stashed file");
            return null;
        }
    }

    /// <summary>JPEG, quality 75; transparent parts become white (JPEG has no transparency).</summary>
    private static byte[] Jpeg(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);
        var rgb = new byte[width * 3 * height];
        for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3)
        {
            var alpha = pixels[i + 3];
            for (var c = 0; c < 3; c++) rgb[j + c] = (byte)((pixels[i + c] * alpha + 255 * (255 - alpha)) / 255);
        }
        var opaque = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, rgb, width * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
        encoder.Frames.Add(BitmapFrame.Create(opaque));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    /// <summary>WIC needs to seek; a stream that cannot is read into memory (at most 128 MB).</summary>
    private static Stream Seekable(Stream stream)
    {
        if (stream.CanSeek) return stream;
        using (stream)
        {
            var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (memory.Length + read > 128L * 1024 * 1024) throw new NotSupportedException("The picture is too large for a thumbnail.");
                memory.Write(buffer, 0, read);
            }
            memory.Position = 0;
            return memory;
        }
    }

    private BitmapSource? LoadPicture(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or COMException)
        {
            _logger.LogDebug(ex, "The photo could not go on the clipboard as a picture; the file still does");
            return null;
        }
    }

    private static string Downloads()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}
