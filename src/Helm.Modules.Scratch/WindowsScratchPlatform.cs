using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Avalonia.Platform.Storage;
using Helm.Core.Desktop;
using Helm.Core.Settings;
using Helm.Ui.Windows;
using Microsoft.Extensions.Logging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Helm.Modules.Scratch;

/// <summary>
/// The Windows side of Scratch: file dialogs, thumbnails (WIC for pictures, the shell for videos), the default app for
/// opening, and the clipboard for Copy (as files, plus the picture itself for a photo).
/// </summary>
internal sealed class WindowsScratchPlatform : IScratchPlatform
{
    private const int ThumbnailSide = 360;
    private readonly ILogger _logger;

    public WindowsScratchPlatform(HelmPaths paths, ILogger<WindowsScratchPlatform> logger)
    {
        _logger = logger;
        OpenFolder = Path.Combine(paths.Root, "cache", ScratchIds.ModuleId, "open");
        try
        {
            // Out of Windows Search and hidden: the copies are temporary and may be private.
            var root = Directory.CreateDirectory(OpenFolder);
            root.Attributes |= FileAttributes.NotContentIndexed | FileAttributes.Hidden;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prepare Scratch's open folder");
        }
    }

    public string OpenFolder { get; }

    public string SendLabel => "Copy";

    public async Task<IReadOnlyList<ScratchSource>> PickFilesAsync(CancellationToken ct)
    {
        var paths = await DesktopWindows.PickFilesAsync("Add to Scratch", many: true).ConfigureAwait(true);
        return paths.Where(File.Exists).Select(FromPath).ToList();
    }

    /// <summary>A file on disk (picked, dropped or copied in Explorer).</summary>
    public static ScratchSource FromPath(string path) =>
        new(Path.GetFileName(path), ScratchFormat.MediaTypeOf(path), new FileInfo(path).Length,
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true), path);

    /// <summary>A picture from the clipboard (a screenshot), as PNG.</summary>
    public static async Task<ScratchSource?> FromClipboardImageAsync(DateTimeOffset now)
    {
        if (!WindowsClipboard.ContainsImage() || WindowsClipboard.GetImage() is not { Length: > 0 } image) return null;
        var bytes = IsPng(image) ? image : await ToPngAsync(image).ConfigureAwait(false);
        return new ScratchSource(ScratchFormat.NameFor("image/png", now), "image/png", bytes.Length, () => new MemoryStream(bytes, writable: false));
    }

    public Task<byte[]?> ThumbnailAsync(ScratchSource source, CancellationToken ct) => Task.Run(async () =>
    {
        var mediaType = ScratchFormat.PickMediaType(source.MediaType, source.Name);
        if (mediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            try
            {
                using var stream = Seekable(source.OpenRead());
                using var random = stream.AsRandomAccessStream();
                return await ThumbnailAsync(random, ExifOrientationMode.RespectExifOrientation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // HEIC without the codec, a damaged file: the shell may still have a thumbnail.
                _logger.LogDebug(ex, "WIC could not decode a picture for its thumbnail");
            }
        }
        return source.Origin is string path && File.Exists(path) ? await ShellThumbnailAsync(path).ConfigureAwait(false) : null;
    }, ct);

    public Task OpenAsync(ScratchLocalFile file, CancellationToken ct)
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

    public async Task<string?> SaveAsync(ScratchLocalFile file, CancellationToken ct)
    {
        if (DesktopWindows.Storage() is not { } storage) return null;
        var extension = Path.GetExtension(file.Name);
        IReadOnlyList<FilePickerFileType> types = extension.Length > 0
            ? [new FilePickerFileType($"{extension.TrimStart('.').ToUpperInvariant()} file") { Patterns = [$"*{extension}"] }, FilePickerFileTypes.All]
            : [FilePickerFileTypes.All];
        var picked = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save a copy",
            SuggestedFileName = file.Name,
            FileTypeChoices = types,
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(Downloads()).ConfigureAwait(true),
            ShowOverwritePrompt = true,
        }).ConfigureAwait(true);
        if (picked?.TryGetLocalPath() is not { } target) return null;
        await using (var from = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var to = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await from.CopyToAsync(to, ct).ConfigureAwait(true);
        return Path.GetDirectoryName(target) ?? target;
    }

    /// <summary>
    /// The files go on the clipboard as files (paste into Explorer, a chat or an email); a single photo also as a
    /// picture, for apps that take only pictures.
    /// </summary>
    public async Task<string?> SendAsync(IReadOnlyList<ScratchLocalFile> files, CancellationToken ct)
    {
        await SetClipboardAsync(files).ConfigureAwait(true);
        var what = files.Count == 1 ? $"“{files[0].Name}”" : $"{files.Count} files";
        return $"Copied {what}. Paste it into a folder, a chat or an email.";
    }

    public Task CopyToClipboardAsync(IReadOnlyList<ScratchLocalFile> files, CancellationToken ct) => SetClipboardAsync(files);

    private async Task SetClipboardAsync(IReadOnlyList<ScratchLocalFile> files)
    {
        var picture = files is [{ } single] && single.MediaType.StartsWith("image/", StringComparison.Ordinal)
            ? await LoadPictureAsync(single.Path).ConfigureAwait(true)
            : null;
        // The window in front owns what goes on the clipboard (none: Windows keeps it without an owner).
        var owner = DesktopWindows.Active?.TryGetPlatformHandle()?.Handle ?? 0;
        FileClipboard.SetFiles(files.Select(f => f.Path).ToList(), picture, owner);
    }

    // ---- Pictures (WIC through Windows.Graphics.Imaging) ---------------------------------------------------------

    /// <summary>
    /// Decoded at thumbnail size (not full size), turned upright as the camera recorded it when
    /// <paramref name="orientation"/> says so, then as JPEG.
    /// </summary>
    private static async Task<byte[]> ThumbnailAsync(IRandomAccessStream stream, ExifOrientationMode orientation)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1.0, (double)ThumbnailSide / Math.Max(1, Math.Max(decoder.PixelWidth, decoder.PixelHeight)));
        // Scaling comes before the turn, so the sizes are the stored picture's.
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, orientation,
            ColorManagementMode.DoNotColorManage);
        return await JpegAsync(Pixels(bitmap), bitmap.PixelWidth, bitmap.PixelHeight).ConfigureAwait(false);
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
            return await ThumbnailAsync(thumbnail, ExifOrientationMode.IgnoreExifOrientation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No shell thumbnail for a file in Scratch");
            return null;
        }
    }

    /// <summary>
    /// JPEG, quality 80 or lower until it fits in a record (<see cref="ScratchItem.MaxThumbnailBytes"/>); transparent
    /// parts become white (JPEG has no transparency).
    /// </summary>
    private static async Task<byte[]> JpegAsync(byte[] bgra, int width, int height)
    {
        var opaque = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            for (var c = 0; c < 3; c++) opaque[i + c] = (byte)((bgra[i + c] * alpha + 255 * (255 - alpha)) / 255);
            opaque[i + 3] = 255;
        }
        byte[] bytes = [];
        foreach (var quality in new[] { 80, 70, 60, 50 })
        {
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(quality / 100f, PropertyType.Single) };
            bytes = await EncodeAsync(BitmapEncoder.JpegEncoderId, opaque, width, height, BitmapAlphaMode.Ignore, options).ConfigureAwait(false);
            if (bytes.Length <= ScratchItem.MaxThumbnailBytes) break;
        }
        return bytes;
    }

    private static async Task<byte[]> EncodeAsync(Guid encoderId, byte[] bgra, int width, int height, BitmapAlphaMode alpha,
        BitmapPropertySet? options = null)
    {
        using var memory = new MemoryStream();
        using (var output = memory.AsRandomAccessStream())
        {
            var encoder = options is null
                ? await BitmapEncoder.CreateAsync(encoderId, output)
                : await BitmapEncoder.CreateAsync(encoderId, output, options);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, alpha, (uint)width, (uint)height, 96, 96, bgra);
            await encoder.FlushAsync();
        }
        return memory.ToArray();
    }

    /// <summary>A copied picture that is not PNG (the bitmap, as a .bmp file) made into PNG.</summary>
    private static async Task<byte[]> ToPngAsync(byte[] image)
    {
        using var random = new MemoryStream(image, writable: false).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(random);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
        return await EncodeAsync(BitmapEncoder.PngEncoderId, Pixels(bitmap), bitmap.PixelWidth, bitmap.PixelHeight, BitmapAlphaMode.Straight)
            .ConfigureAwait(false);
    }

    private static bool IsPng(byte[] bytes) => bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', ..];

    private static byte[] Pixels(SoftwareBitmap bitmap)
    {
        var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyToBuffer(buffer);
        return buffer.ToArray();
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

    /// <summary>The photo at full size for the clipboard; null when it cannot be decoded (the file still goes there).</summary>
    private async Task<FileClipboard.Picture?> LoadPictureAsync(string path)
    {
        try
        {
            return await Task.Run(async () =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var random = file.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(random);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                return new FileClipboard.Picture(bitmap.PixelWidth, bitmap.PixelHeight, Pixels(bitmap));
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
