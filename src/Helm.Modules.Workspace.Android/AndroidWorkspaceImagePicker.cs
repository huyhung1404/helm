using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Provider;
using Helm.Core.Platform;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;
using Path = System.IO.Path;
using Stream = System.IO.Stream;

namespace Helm.Modules.Workspace;

/// <summary>
/// The camera and the gallery for pictures sent to Claude. The camera app writes the photo into Helm's cache through
/// the FileProvider (Helm declares no camera permission, so none is asked); the gallery is Android's photo picker
/// (no storage permission either). Each picture is turned upright as the camera recorded it, brought down to
/// <see cref="WorkspaceImages.MaxSide"/> on its longer side and saved as JPEG.
/// </summary>
internal sealed class AndroidWorkspaceImagePicker(ILogger<AndroidWorkspaceImagePicker> logger) : IWorkspaceImagePicker
{
    /// <summary>The FileProvider declared by the app (see update_paths.xml, "workspace-camera").</summary>
    private static string Authority => Context.PackageName + ".updates";

    private static Context Context => AndroidApp.Context;

    public async Task<PickedImage?> TakePhotoAsync(CancellationToken ct)
    {
        var folder = Path.Combine(Context.CacheDir!.AbsolutePath, "workspace-camera");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder)) TryDelete(old);
        var file = Path.Combine(folder, $"photo-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jpg");
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(Context, Authority, new Java.IO.File(file))!;
        var intent = new Intent(MediaStore.ActionImageCapture);
        intent.PutExtra(MediaStore.ExtraOutput, uri);
        intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);
        // The camera app needs the grant in the clip data too on some phones.
        intent.ClipData = ClipData.NewRawUri("photo", uri);
        try
        {
            var outcome = await ActivityHost.StartForResultAsync(intent).ConfigureAwait(true);
            if (!outcome.Ok || !File.Exists(file) || new FileInfo(file).Length == 0) return null;
            return await Task.Run(() => Prepare(() => File.OpenRead(file)), ct).ConfigureAwait(true);
        }
        finally
        {
            TryDelete(file);
        }
    }

    public async Task<IReadOnlyList<PickedImage>> PickAsync(int max, CancellationToken ct)
    {
        if (max <= 0) return [];
        Intent intent;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            intent = new Intent(MediaStore.ActionPickImages);
            if (max > 1) intent.PutExtra(MediaStore.ExtraPickImagesMax, Math.Min(max, MediaStore.PickImagesMaxLimit));
        }
        else
        {
            intent = new Intent(Intent.ActionGetContent);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("image/*");
            intent.PutExtra(Intent.ExtraAllowMultiple, max > 1);
        }
        var outcome = await ActivityHost.StartForResultAsync(intent).ConfigureAwait(true);
        if (!outcome.Ok || outcome.Data is not { } data) return [];
        var uris = new List<AndroidUri>();
        if (data.ClipData is { } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++)
                if (clip.GetItemAt(i)?.Uri is { } u) uris.Add(u);
        }
        else if (data.Data is { } single)
        {
            uris.Add(single);
        }
        var resolver = Context.ContentResolver!;
        return await Task.Run(() => uris.Take(max)
            .Select(u => Prepare(() => resolver.OpenInputStream(u) ?? throw new IOException("The picture cannot be read.")))
            .OfType<PickedImage>()
            .ToList(), ct).ConfigureAwait(true);
    }

    /// <summary>Upright, at most <see cref="WorkspaceImages.MaxSide"/> on its longer side, JPEG; null when it is no picture.</summary>
    private PickedImage? Prepare(Func<Stream> open)
    {
        Bitmap? decoded = null;
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            using (var probe = open()) BitmapFactory.DecodeStream(probe, null, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;
            // Decoded at about twice the size it is sent at (not full size: a 50 MP photo would not fit in memory).
            var sample = 1;
            while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= WorkspaceImages.MaxSide) sample *= 2;
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
                // No EXIF (a PNG, a screenshot): already upright.
            }
            var scale = Math.Min(1.0, (double)WorkspaceImages.MaxSide / Math.Max(decoded.Width, decoded.Height));
            using var matrix = new Matrix();
            matrix.PostScale((float)scale, (float)scale);
            if (degrees != 0) matrix.PostRotate(degrees);
            var ready = scale < 1 || degrees != 0
                ? Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true)
                : decoded;
            try
            {
                using var memory = new MemoryStream();
                ready.Compress(Bitmap.CompressFormat.Jpeg!, WorkspaceImages.JpegQuality, memory);
                return new PickedImage(memory.ToArray(), "image/jpeg", ready.Width, ready.Height);
            }
            finally
            {
                if (!ReferenceEquals(ready, decoded)) ready.Recycle();
            }
        }
        catch (Exception ex) when (ex is IOException or Java.Lang.Exception or OutOfMemoryException)
        {
            logger.LogInformation(ex, "Workspace: a picture could not be prepared for sending");
            return null;
        }
        finally
        {
            decoded?.Recycle();
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
