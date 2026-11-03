using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Provider;
using Android.Views;
using Android.Widget;
using Helm.Ai.Assistant;
using Microsoft.Extensions.Logging;
using Path = System.IO.Path;
using Stream = System.IO.Stream;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// Photo to data in Ask Helm's card: the camera beside the microphone takes a photo (the camera app, through Helm's
/// FileProvider: no camera permission) or picks one (Android's photo picker: no storage permission). The photo,
/// with a sentence said or typed after it, goes to the model with Helm's tools; what it saved is listed with Undo,
/// as for words. The photo is sent upright and small (<see cref="AssistantPhotos"/>) and kept only for the turn:
/// the camera's file is deleted once read, the bytes go when the turn has done something or answered.
/// </summary>
public partial class AskHelmActivity
{
    private const int CameraRequest = 42;
    private const int GalleryRequest = 43;
    private const int ThumbnailSide = 360;

    private AssistantImage? _photo;
    private ImageView _photoView = null!;
    private LinearLayout _controls = null!;
    private string? _cameraFile;

    /// <summary>The thumbnail of the photo being asked about, under the question; hidden without one.</summary>
    private View BuildPhoto()
    {
        _photoView = new ImageView(this) { Visibility = ViewStates.Gone, ContentDescription = "Ảnh" };
        _photoView.SetScaleType(ImageView.ScaleType.FitStart);
        _photoView.SetAdjustViewBounds(true);
        _photoView.SetMaxHeight(Dp(140));
        return _photoView;
    }

    /// <summary>The camera button, right of the microphone (Aa · mic · camera).</summary>
    private View CameraButton()
    {
        var camera = new ImageView(this);
        camera.SetImageResource(Resource.Drawable.ask_card_camera);
        camera.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(_colors.Secondary);
        camera.SetScaleType(ImageView.ScaleType.Center);
        camera.Background = Outline(_colors.Line, 24);
        camera.ContentDescription = "Ảnh";
        camera.Click += (_, _) => ChoosePhoto();
        // A text selection is worked on as text: no photo there.
        if (Intent?.Action == Intent.ActionProcessText) camera.Visibility = ViewStates.Gone;
        return camera;
    }

    /// <summary>"Chụp ảnh" or "Thư viện".</summary>
    /// <summary>The widget's camera button opens the card straight on the photo choice.</summary>
    partial void OpenCamera(ref bool opened)
    {
        ChoosePhoto();
        opened = true;
    }

    private void ChoosePhoto()
    {
        StopAutoClose();
        if (_busy) return;
        _recognizer?.Cancel();
        _listening = false;
        DropPhoto();
        ClearResult();
        ShowContext();
        _heard.Text = "";
        _heard.Hint = "";
        _status.Text = "";
        _controls.Visibility = ViewStates.Gone;
        Buttons(("Chụp ảnh", TakePhoto, true), ("Thư viện", PickFromGallery, false));
    }

    private void TakePhoto()
    {
        try
        {
            var folder = Path.Combine(CacheDir!.AbsolutePath, "ask-camera");
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.EnumerateFiles(folder)) TryDelete(old);
            _cameraFile = Path.Combine(folder, $"photo-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jpg");
            // The FileProvider declared by the app (update_paths.xml, "ask-camera").
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this, PackageName + ".updates", new Java.IO.File(_cameraFile))!;
            var intent = new Intent(MediaStore.ActionImageCapture);
            intent.PutExtra(MediaStore.ExtraOutput, uri);
            intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);
            // The camera app needs the grant in the clip data too on some phones.
            intent.ClipData = ClipData.NewRawUri("photo", uri);
            StartActivityForResult(intent, CameraRequest);
        }
        catch (Exception ex) when (ex is ActivityNotFoundException or IOException or Java.Lang.Exception)
        {
            _logger?.LogInformation(ex, "Ask Helm: the camera did not open");
            PhotoCancelled("Không mở được camera: Anh chọn ảnh trong Thư viện nha.");
        }
    }

    private void PickFromGallery()
    {
        Intent intent;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            intent = new Intent(MediaStore.ActionPickImages);
        }
        else
        {
            intent = new Intent(Intent.ActionGetContent);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("image/*");
        }
        try
        {
            StartActivityForResult(intent, GalleryRequest);
        }
        catch (ActivityNotFoundException ex)
        {
            _logger?.LogInformation(ex, "Ask Helm: no photo picker");
            PhotoCancelled("Máy này không có trình chọn ảnh.");
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode is not (CameraRequest or GalleryRequest)) return;
        var file = _cameraFile;
        _cameraFile = null;
        Func<Stream>? open = null;
        if (resultCode == Result.Ok)
        {
            if (requestCode == CameraRequest && file is not null && File.Exists(file) && new FileInfo(file).Length > 0) open = () => File.OpenRead(file);
            else if (requestCode == GalleryRequest && data?.Data is { } uri && ContentResolver is { } resolver)
                open = () => resolver.OpenInputStream(uri) ?? throw new IOException("The photo cannot be read.");
        }
        if (open is null)
        {
            if (file is not null) TryDelete(file);
            PhotoCancelled(null);
            return;
        }
        _ = ReadPickedAsync(open, file);
    }

    private async Task ReadPickedAsync(Func<Stream> open, string? file)
    {
        _status.Text = "Em đang mở ảnh…";
        (AssistantImage Image, Bitmap Thumbnail)? ready;
        try
        {
            ready = await Task.Run(() => Prepare(open));
        }
        finally
        {
            // The camera's copy goes as soon as it is read: only the small JPEG is kept, for this turn.
            if (file is not null) TryDelete(file);
        }
        if (IsFinishing || IsDestroyed) return;
        if (ready is not { } photo)
        {
            PhotoCancelled("Em không đọc được ảnh này: Anh thử ảnh khác nha.");
            return;
        }
        _photo = photo.Image;
        _photoView.SetImageBitmap(photo.Thumbnail);
        _photoView.Visibility = ViewStates.Visible;
        ClearResult();
        ShowContext();
        _heard.Text = "";
        _heard.Hint = "";
        _status.Text = "Gửi luôn, hoặc chạm micro để nói thêm (vd: “chia đôi với Nam”).";
        _controls.Visibility = ViewStates.Visible;
        Buttons(("Gửi ảnh", () => _ = SendPhotoAsync(""), true));
    }

    /// <summary>Back to the card as it was: no photo came.</summary>
    private void PhotoCancelled(string? why)
    {
        ClearResult();
        ShowContext();
        _controls.Visibility = ViewStates.Visible;
        _status.Text = why ?? "";
    }

    /// <summary>What was said or typed while a photo waits: sent with it. False: no photo, a turn of words.</summary>
    private bool SendWithPhoto(string text)
    {
        if (_photo is null)
        {
            _photoView.Visibility = ViewStates.Gone;
            return false;
        }
        _ = SendPhotoAsync(text);
        return true;
    }

    private async Task SendPhotoAsync(string said)
    {
        if (_session is null || _busy || _photo is not { } photo) return;
        _busy = true;
        _recognizer?.Cancel();
        _listening = false;
        ClearResult();
        _controls.Visibility = ViewStates.Visible;
        _status.Text = $"{Short(_session.Model?.Name ?? "")} đang đọc ảnh…";
        _mic.Visibility = ViewStates.Invisible;
        AssistantReply reply;
        try
        {
            var session = _session;
            reply = await Task.Run(() => session.SendPhotoAsync(photo, said, ConfirmAsync, _cancel.Token));
        }
        catch (System.OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Ask Helm could not read a photo");
            reply = new AssistantReply("", [], false, ex.Message);
        }
        finally
        {
            _busy = false;
        }
        if (IsFinishing || IsDestroyed) return;
        _mic.Visibility = ViewStates.Visible;
        // Kept only while the turn is not over: it failed before doing anything ("Thử lại"), or em asked something back.
        var unfinished = reply.Done.Count == 0 && (reply.Problem is not null || reply.Text.TrimEnd().EndsWith('?'));
        if (!unfinished) DropPhoto();
        if (reply.Done.Count > 0 && _contextNotification != 0)
        {
            ((NotificationManager?)GetSystemService(NotificationService))?.Cancel(_contextNotification);
            _contextNotification = 0;
        }
        _context = null;
        if (reply.Problem is { } problem)
        {
            ShowProblem(problem, reply.Done);
            if (_photo is not null) Buttons(("Thử lại", () => _ = SendPhotoAsync(said), true), ("Đóng", Finish, false));
            return;
        }
        ShowReply(reply);
        // A question back listens for the answer (it goes with the photo); else say which model read it.
        if (_listening) return;
        var ranOn = $"{Short(_session.PhotoModel?.Name ?? _session.Model?.Name ?? "")} qua API";
        _status.Text = reply.Done.Count > 0 ? $"Đã lưu · {ranOn} · tự đóng sau vài giây" : ranOn;
    }

    /// <summary>The photo is not kept: the next turn is words only (the thumbnail stays until then).</summary>
    private void DropPhoto() => _photo = null;

    /// <summary>Upright, at most <see cref="AssistantPhotos.MaxSide"/> on its longer side, JPEG, and a thumbnail; null when it is no picture.</summary>
    private (AssistantImage Image, Bitmap Thumbnail)? Prepare(Func<Stream> open)
    {
        Bitmap? decoded = null;
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            using (var probe = open()) BitmapFactory.DecodeStream(probe, null, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;
            var sample = AssistantPhotos.SampleSize(bounds.OutWidth, bounds.OutHeight);
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
            var (width, height) = AssistantPhotos.Fit(decoded.Width, decoded.Height);
            using var matrix = new Matrix();
            matrix.PostScale((float)width / decoded.Width, (float)height / decoded.Height);
            if (degrees != 0) matrix.PostRotate(degrees);
            var ready = width != decoded.Width || degrees != 0
                ? Bitmap.CreateBitmap(decoded, 0, 0, decoded.Width, decoded.Height, matrix, true)
                : decoded;
            try
            {
                using var memory = new MemoryStream();
                ready.Compress(Bitmap.CompressFormat.Jpeg!, AssistantPhotos.JpegQuality, memory);
                var scale = Math.Min(1.0, (double)ThumbnailSide / Math.Max(ready.Width, ready.Height));
                var thumbnail = Bitmap.CreateScaledBitmap(ready, Math.Max(1, (int)(ready.Width * scale)), Math.Max(1, (int)(ready.Height * scale)), true)!;
                // A small photo comes back as itself, and that one is recycled below.
                if (ReferenceEquals(thumbnail, ready) || ReferenceEquals(thumbnail, decoded)) thumbnail = thumbnail.Copy(Bitmap.Config.Argb8888!, false)!;
                return (new AssistantImage(memory.ToArray(), "image/jpeg"), thumbnail);
            }
            finally
            {
                if (!ReferenceEquals(ready, decoded)) ready.Recycle();
            }
        }
        catch (Exception ex) when (ex is IOException or Java.Lang.Exception or OutOfMemoryException)
        {
            _logger?.LogInformation(ex, "Ask Helm: a photo could not be prepared");
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
