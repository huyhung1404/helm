using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Provider;
using Helm.Core.Platform;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using AndroidEnvironment = Android.OS.Environment;
using AndroidUri = Android.Net.Uri;
using Path = System.IO.Path;
using Stream = System.IO.Stream;

namespace Helm.Modules.Scratch;

/// <summary>
/// The Android side of Scratch: the Storage Access Framework picker (several files at once), thumbnails from the
/// system (photos and videos), the viewer intent, MediaStore for saving (Pictures, Movies, Music or Download, in a
/// "Helm" folder, no permission needed on Android 10+) and the share sheet.
/// </summary>
internal sealed class AndroidScratchPlatform : IScratchPlatform
{
    private const int ThumbnailSide = 360;

    /// <summary>The FileProvider declared by the app (see update_paths.xml, "scratch-open").</summary>
    private static string Authority => Context.PackageName + ".updates";

    private readonly ILogger _logger;

    public AndroidScratchPlatform(ILogger<AndroidScratchPlatform> logger)
    {
        _logger = logger;
        // Cache (not files): Android may also clear it, and it is where the FileProvider shares "scratch-open/".
        OpenFolder = Path.Combine(Context.CacheDir!.AbsolutePath, "scratch-open");
    }

    private static Context Context => AndroidApp.Context;

    public string OpenFolder { get; }

    public string SendLabel => "Share";

    public async Task<IReadOnlyList<ScratchSource>> PickFilesAsync(CancellationToken ct)
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        var outcome = await ActivityHost.StartForResultAsync(intent).ConfigureAwait(true);
        if (!outcome.Ok || outcome.Data is not { } data) return [];
        var uris = new List<AndroidUri>();
        if (data.ClipData is { } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++)
                if (clip.GetItemAt(i)?.Uri is { } uri) uris.Add(uri);
        }
        else if (data.Data is { } single)
        {
            uris.Add(single);
        }
        return uris.Select(FromUri).ToList();
    }

    /// <summary>A file from the picker or the share sheet. Its name, size and type come from its provider.</summary>
    public static ScratchSource FromUri(AndroidUri uri)
    {
        var resolver = Context.ContentResolver!;
        string? name = null;
        long? size = null;
        try
        {
            using var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName, IOpenableColumns.Size], null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                name = cursor.IsNull(0) ? null : cursor.GetString(0);
                size = cursor.IsNull(1) ? null : cursor.GetLong(1);
            }
        }
        catch (Exception)
        {
            // Some apps share URIs that answer no queries; the name then comes from the URI or the type.
        }
        string? type = null;
        try
        {
            type = resolver.GetType(uri);
        }
        catch (Exception)
        {
        }
        name ??= uri.Scheme == "file" ? uri.LastPathSegment : null;
        return new ScratchSource(name ?? "", type, size,
            () => resolver.OpenInputStream(uri) ?? throw new IOException("The file cannot be read."), uri);
    }

    public Task<byte[]?> ThumbnailAsync(ScratchSource source, CancellationToken ct) => Task.Run(() =>
    {
        var mediaType = ScratchFormat.PickMediaType(source.MediaType, source.Name);
        Bitmap? bitmap = null;
        if (source.Origin is AndroidUri uri && OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            try
            {
                // The system's own thumbnail: fast, and turned upright for photos.
                bitmap = Context.ContentResolver!.LoadThumbnail(uri, new Android.Util.Size(ThumbnailSide, ThumbnailSide), null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No system thumbnail for a file in Scratch");
            }
        }
        if (bitmap is null && mediaType.StartsWith("image/", StringComparison.Ordinal)) bitmap = PictureThumbnail(source.OpenRead);
        if (bitmap is null && mediaType.StartsWith("video/", StringComparison.Ordinal) && source.Origin is AndroidUri video) bitmap = VideoFrame(video);
        if (bitmap is null) return null;
        try
        {
            return Jpeg(bitmap);
        }
        finally
        {
            bitmap.Recycle();
        }
    }, ct);

    public Task OpenAsync(ScratchLocalFile file, CancellationToken ct)
    {
        var view = new Intent(Intent.ActionView);
        view.SetDataAndType(ProviderUri(file), file.MediaType);
        view.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        try
        {
            Context.StartActivity(Intent.CreateChooser(view, file.Name)!.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission));
        }
        catch (ActivityNotFoundException)
        {
            throw new InvalidOperationException($"No app on this phone opens {Path.GetExtension(file.Name)} files. Use Share or Save to phone instead.");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Android 10+: into Pictures/Helm, Movies/Helm, Music/Helm or Download/Helm, where the gallery and the file
    /// manager see it. Older phones ask where to save.
    /// </summary>
    public async Task<string?> SaveAsync(ScratchLocalFile file, CancellationToken ct)
    {
        var resolver = Context.ContentResolver!;
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType(file.MediaType);
            intent.PutExtra(Intent.ExtraTitle, file.Name);
            var outcome = await ActivityHost.StartForResultAsync(intent).ConfigureAwait(true);
            if (!outcome.Ok || outcome.Data?.Data is not { } picked) return null;
            await using (var to = resolver.OpenOutputStream(picked, "wt") ?? throw new IOException("The file cannot be written."))
            await using (var from = File.OpenRead(file.Path))
                await from.CopyToAsync(to, ct).ConfigureAwait(true);
            return "the folder you chose";
        }

        var (collection, folder) = file.MediaType switch
        {
            var t when t.StartsWith("image/", StringComparison.Ordinal) => (MediaStore.Images.Media.ExternalContentUri!, AndroidEnvironment.DirectoryPictures!),
            var t when t.StartsWith("video/", StringComparison.Ordinal) => (MediaStore.Video.Media.ExternalContentUri!, AndroidEnvironment.DirectoryMovies!),
            var t when t.StartsWith("audio/", StringComparison.Ordinal) => (MediaStore.Audio.Media.ExternalContentUri!, AndroidEnvironment.DirectoryMusic!),
            _ => (MediaStore.Downloads.ExternalContentUri!, AndroidEnvironment.DirectoryDownloads!),
        };
        var relative = $"{folder}/Helm";
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, file.Name);
        values.Put(MediaStore.IMediaColumns.MimeType, file.MediaType);
        values.Put(MediaStore.IMediaColumns.RelativePath, relative);
        // Hidden from other apps until the copy is complete.
        values.Put(MediaStore.IMediaColumns.IsPending, 1);
        var target = resolver.Insert(collection, values) ?? throw new IOException("The phone's storage did not accept the file.");
        try
        {
            await using (var to = resolver.OpenOutputStream(target, "w") ?? throw new IOException("The file cannot be written."))
            await using (var from = File.OpenRead(file.Path))
                await from.CopyToAsync(to, ct).ConfigureAwait(true);
            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(target, values, null, null);
        }
        catch
        {
            resolver.Delete(target, null, null);
            throw;
        }
        return relative;
    }

    /// <summary>
    /// The file as a content URI on the clipboard: apps that paste files (chats, mail) get it; the app that pastes is
    /// allowed to read it through Helm's FileProvider.
    /// </summary>
    public Task CopyToClipboardAsync(IReadOnlyList<ScratchLocalFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return Task.CompletedTask;
        if (Context.GetSystemService(Context.ClipboardService) is not ClipboardManager clipboard)
            throw new InvalidOperationException("This phone has no clipboard Helm can use.");
        var clip = ClipData.NewUri(Context.ContentResolver, files[0].Name, ProviderUri(files[0]))!;
        foreach (var file in files.Skip(1)) clip.AddItem(new ClipData.Item(ProviderUri(file)));
        clipboard.PrimaryClip = clip;
        return Task.CompletedTask;
    }

    public Task<string?> SendAsync(IReadOnlyList<ScratchLocalFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return Task.FromResult<string?>(null);
        var uris = files.Select(ProviderUri).ToList();
        Intent send;
        if (uris.Count == 1)
        {
            send = new Intent(Intent.ActionSend);
            send.PutExtra(Intent.ExtraStream, uris[0]);
        }
        else
        {
            send = new Intent(Intent.ActionSendMultiple);
            send.PutParcelableArrayListExtra(Intent.ExtraStream, uris.Cast<IParcelable>().ToList());
        }
        send.SetType(CommonType(files.Select(f => f.MediaType)));
        // The grant covers every URI only when they are in the clip data too.
        var clip = ClipData.NewRawUri(files[0].Name, uris[0])!;
        foreach (var uri in uris.Skip(1)) clip.AddItem(new ClipData.Item(uri));
        send.ClipData = clip;
        send.AddFlags(ActivityFlags.GrantReadUriPermission);
        var title = files.Count == 1 ? files[0].Name : $"{files.Count} files";
        Context.StartActivity(Intent.CreateChooser(send, title)!.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission));
        return Task.FromResult<string?>(null);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private static AndroidUri ProviderUri(ScratchLocalFile file) =>
        AndroidX.Core.Content.FileProvider.GetUriForFile(Context, Authority, new Java.IO.File(file.Path))!;

    /// <summary>"image/jpeg" for one type, "image/*" for several images, "*/*" for a mix.</summary>
    private static string CommonType(IEnumerable<string> types)
    {
        var distinct = types.Select(t => t.ToLowerInvariant()).Distinct().ToList();
        if (distinct.Count == 1) return distinct[0];
        var families = distinct.Select(t => t.Split('/')[0]).Distinct().ToList();
        return families.Count == 1 ? $"{families[0]}/*" : "*/*";
    }

    /// <summary>Decoded at about thumbnail size (not full size) and turned upright as the camera recorded it.</summary>
    private Bitmap? PictureThumbnail(Func<Stream> open)
    {
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            using (var probe = open()) BitmapFactory.DecodeStream(probe, null, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;
            var sample = 1;
            while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= ThumbnailSide) sample *= 2;
            Bitmap? decoded;
            using (var stream = open()) decoded = BitmapFactory.DecodeStream(stream, null, new BitmapFactory.Options { InSampleSize = sample });
            if (decoded is null) return null;
            var degrees = 0;
            try
            {
                using var stream = open();
                degrees = new ExifInterface(stream).GetAttributeInt(ExifInterface.TagOrientation, 1) switch
                {
                    3 => 180,
                    6 => 90,
                    8 => 270,
                    _ => 0,
                };
            }
            catch (Exception)
            {
                // No EXIF (PNG, a screenshot): already upright.
            }
            if (degrees == 0) return decoded;
            using var matrix = new Matrix();
            matrix.PostRotate(degrees);
            var upright = Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true);
            if (!ReferenceEquals(upright, decoded)) decoded.Recycle();
            return upright;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not decode a picture for its thumbnail");
            return null;
        }
    }

    private Bitmap? VideoFrame(AndroidUri uri)
    {
        var retriever = new MediaMetadataRetriever();
        try
        {
            retriever.SetDataSource(Context, uri);
            // One second in, past black first frames; the first frame when the video is shorter.
            return retriever.GetFrameAtTime(1_000_000, Option.ClosestSync) ?? retriever.GetFrameAtTime(0);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read a frame of a video for its thumbnail");
            return null;
        }
        finally
        {
            retriever.Release();
        }
    }

    /// <summary>At most <see cref="ThumbnailSide"/> on the longer side, JPEG quality 80 or lower until it fits in a record.</summary>
    private static byte[] Jpeg(Bitmap bitmap)
    {
        var scale = Math.Min(1.0, (double)ThumbnailSide / Math.Max(bitmap.Width, bitmap.Height));
        var sized = scale < 1
            ? Bitmap.CreateScaledBitmap(bitmap, Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)), true)!
            : bitmap;
        try
        {
            byte[] bytes = [];
            foreach (var quality in new[] { 80, 70, 60, 50 })
            {
                using var memory = new MemoryStream();
                sized.Compress(Bitmap.CompressFormat.Jpeg!, quality, memory);
                bytes = memory.ToArray();
                if (bytes.Length <= ScratchItem.MaxThumbnailBytes) break;
            }
            return bytes;
        }
        finally
        {
            if (!ReferenceEquals(sized, bitmap)) sized.Recycle();
        }
    }
}
