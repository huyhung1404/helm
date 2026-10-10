using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Speech.Tts;
using Helm.Core.Platform;
using Helm.Modules.NovelReader.Playback;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;
using Path = System.IO.Path;

namespace Helm.Modules.NovelReader.Platform;

/// <summary>
/// The Android side of Novel Reader's pages: the Storage Access Framework picker (a novel, dictionary files, a cover
/// picture), copies of picked files in the cache (the shared code reads files by path), and the phone's settings for
/// voices and battery.
/// </summary>
internal sealed class AndroidNovelPlatform(AndroidTtsEngine voices, ILogger<AndroidNovelPlatform> logger) : INovelPlatform
{
    private const int CoverSide = 600;

    private static Context Context => AndroidApp.Context;

    public async Task<string?> PickNovelAsync(CancellationToken ct)
    {
        var picked = await PickAsync(["text/plain", "application/octet-stream"], multiple: false).ConfigureAwait(true);
        return picked.Count == 0 ? null : await CopyAsync(picked[0], "novel-import", ".txt", ct).ConfigureAwait(true);
    }

    public async Task<IReadOnlyList<string>> PickDictionariesAsync(CancellationToken ct)
    {
        var picked = await PickAsync(["text/plain", "application/octet-stream"], multiple: true).ConfigureAwait(true);
        var folder = Path.Combine(Context.CacheDir!.AbsolutePath, "novel-dictionaries-import");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        var paths = new List<string>();
        foreach (var uri in picked) paths.Add(await CopyAsync(uri, "novel-dictionaries-import", ".txt", ct).ConfigureAwait(true));
        return paths;
    }

    public async Task<byte[]?> PickCoverAsync(CancellationToken ct)
    {
        var picked = await PickAsync(["image/*"], multiple: false).ConfigureAwait(true);
        if (picked.Count == 0) return null;
        var uri = picked[0];
        return await Task.Run(() => Cover(uri), ct).ConfigureAwait(true);
    }

    public void StartPhoneVoices() => voices.EnsureStarted();

    public void PreviewPhoneVoice(SpeechVoice voice, string text) => voices.Preview(voice, text);

    public void OpenVoiceSettings() => Start(new Intent("com.android.settings.TTS_SETTINGS"), fallback: new Intent(Settings.ActionSettings));

    public void InstallVoiceData() => Start(new Intent(TextToSpeech.Engine.ActionInstallTtsData), fallback: new Intent("com.android.settings.TTS_SETTINGS"));

    public void OpenBatterySettings() => Start(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings), fallback: new Intent(Settings.ActionSettings));

    public bool RunsFreelyInBackground =>
        Context.GetSystemService(Context.PowerService) is PowerManager power && power.IsIgnoringBatteryOptimizations(Context.PackageName);

    private void Start(Intent intent, Intent fallback)
    {
        intent.AddFlags(ActivityFlags.NewTask);
        try
        {
            Context.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            fallback.AddFlags(ActivityFlags.NewTask);
            try
            {
                Context.StartActivity(fallback);
            }
            catch (ActivityNotFoundException ex)
            {
                logger.LogWarning(ex, "No settings screen for {Action}", intent.Action);
            }
        }
    }

    private static async Task<IReadOnlyList<AndroidUri>> PickAsync(string[] types, bool multiple)
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(types.Length == 1 ? types[0] : "*/*");
        if (types.Length > 1) intent.PutExtra(Intent.ExtraMimeTypes, types);
        intent.PutExtra(Intent.ExtraAllowMultiple, multiple);
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
        return uris;
    }

    /// <summary>Copies a picked file into the cache under its own name (Novel Reader takes the title and the dictionary kind from it).</summary>
    private static async Task<string> CopyAsync(AndroidUri uri, string folderName, string extension, CancellationToken ct)
    {
        var resolver = Context.ContentResolver!;
        string? name = null;
        try
        {
            using var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            if (cursor is not null && cursor.MoveToFirst() && !cursor.IsNull(0)) name = cursor.GetString(0);
        }
        catch (Java.Lang.Exception)
        {
            // Some providers answer no queries: the name comes from the URI.
        }
        name = Path.GetFileName(name ?? uri.LastPathSegment ?? "novel");
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        if (name.Length == 0) name = "novel";
        if (Path.GetExtension(name).Length == 0) name += extension;
        var folder = Path.Combine(Context.CacheDir!.AbsolutePath, folderName);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        await using (var from = resolver.OpenInputStream(uri) ?? throw new IOException("The file cannot be read."))
        await using (var to = File.Create(path))
            await from.CopyToAsync(to, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>At most <see cref="CoverSide"/> on the longer side, upright, on white (no transparency), JPEG quality 85.</summary>
    private byte[]? Cover(AndroidUri uri)
    {
        var resolver = Context.ContentResolver!;
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            using (var probe = resolver.OpenInputStream(uri)) BitmapFactory.DecodeStream(probe, null, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;
            var sample = 1;
            while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= CoverSide) sample *= 2;
            Bitmap? decoded;
            using (var stream = resolver.OpenInputStream(uri)) decoded = BitmapFactory.DecodeStream(stream, null, new BitmapFactory.Options { InSampleSize = sample });
            if (decoded is null) return null;
            var degrees = 0;
            try
            {
                using var stream = resolver.OpenInputStream(uri);
                if (stream is not null)
                {
                    degrees = new Android.Media.ExifInterface(stream).GetAttributeInt(Android.Media.ExifInterface.TagOrientation, 1) switch
                    {
                        3 => 180,
                        6 => 90,
                        8 => 270,
                        _ => 0,
                    };
                }
            }
            catch (Java.Lang.Exception)
            {
                // No EXIF (PNG, a screenshot): already upright.
            }
            var scale = Math.Min(1.0, (double)CoverSide / Math.Max(decoded.Width, decoded.Height));
            var width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            var height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
            var turned = degrees is 90 or 270;
            using var cover = Bitmap.CreateBitmap(turned ? height : width, turned ? width : height, Bitmap.Config.Argb8888!)!;
            using (var canvas = new Canvas(cover))
            {
                canvas.DrawColor(Color.White);
                using var matrix = new Matrix();
                matrix.PostScale((float)scale, (float)scale);
                matrix.PostRotate(degrees);
                // Rotating turns the picture around its top-left corner: move it back into the canvas.
                if (degrees == 90) matrix.PostTranslate(height, 0);
                else if (degrees == 180) matrix.PostTranslate(width, height);
                else if (degrees == 270) matrix.PostTranslate(0, width);
                using var paint = new Paint(PaintFlags.FilterBitmap);
                canvas.DrawBitmap(decoded, matrix, paint);
            }
            decoded.Recycle();
            using var memory = new MemoryStream();
            cover.Compress(Bitmap.CompressFormat.Jpeg!, 85, memory);
            cover.Recycle();
            return memory.ToArray();
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            logger.LogWarning(ex, "Could not make a cover from the picture");
            return null;
        }
    }
}
