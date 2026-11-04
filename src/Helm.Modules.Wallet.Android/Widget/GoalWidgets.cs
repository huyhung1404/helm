using Android.Appwidget;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Helm.Core;
using Microsoft.Extensions.DependencyInjection;
using Color = Android.Graphics.Color;
using IntentFilterAttribute = Android.App.IntentFilterAttribute;
using MetaDataAttribute = Android.App.MetaDataAttribute;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;
using RemoteViews = Android.Widget.RemoteViews;
using ViewStates = Android.Views.ViewStates;

namespace Helm.Modules.Wallet;

/// <summary>The "Goal" home-screen widget: a savings goal's photo, lit up from the bottom as money is set aside.</summary>
[BroadcastReceiver(Label = "Goal", Exported = false)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/goal_widget_info")]
public sealed class GoalWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        foreach (var id in appWidgetIds) GoalWidgets.Update(context, appWidgetManager, id);
    }

    /// <summary>Resized: square (the photo and the percentage) or wide (the money and "+ Set aside" too).</summary>
    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? appWidgetManager, int appWidgetId, Bundle? newOptions)
    {
        if (context is null || appWidgetManager is null) return;
        GoalWidgets.Update(context, appWidgetManager, appWidgetId);
    }
}

/// <summary>
/// Draws the Goal widgets: one bitmap (a widget cannot grey part of a picture), drawn again when money is set aside. A
/// tap opens the goal's sheet in Wallet (set money aside, or record the purchase once there is enough). Runs in the app
/// process, possibly without the activity, so services come from <see cref="HelmAndroidServices"/>; the data is
/// <see cref="GoalWidgetModel"/>. The photo is read from the blob store in the background the first time, then kept.
/// </summary>
internal static class GoalWidgets
{
    /// <summary>The item a tap hands to the Wallet page (<see cref="ShellIntents.ExtraItem"/>): "goal:" and its id.</summary>
    public const string ItemPrefix = "goal:";

    // The largest bitmap side, in pixels: sharp at any size, still small for the launcher (widget bitmaps are limited).
    private const int MaxPixels = 900;

    private static readonly object s_gate = new();
    private static readonly Dictionary<string, Bitmap> s_photos = new(StringComparer.Ordinal);
    private static readonly HashSet<string> s_reading = new(StringComparer.Ordinal);

    public static void RefreshAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        var ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(GoalWidgetProvider))));
        if (ids is not { Length: > 0 }) return;
        foreach (var id in ids) Update(context, manager, id);
    }

    public static void Update(Context context, AppWidgetManager manager, int widgetId)
    {
        try
        {
            var (width, height) = SizeOf(manager, widgetId);
            var (model, settings, enabled, book) = Load();
            var views = new RemoteViews(context.PackageName, R.Layout(context, "goal_widget"));
            var night = ((context.Resources?.Configuration?.UiMode ?? UiMode.NightNo) & UiMode.NightMask) == UiMode.NightYes;
            var (main, secondary) = settings.WidgetLook.TextColors ?? (night ? (0xFFFFFFFFu, 0xFFC5C5C5u) : (0xFF1A1A1Au, 0xFF5F5F5Fu));

            var bg = R.Id(context, "goal_widget_bg");
            views.SetInt(bg, "setColorFilter", settings.WidgetLook.BackgroundColor is { } solid ? unchecked((int)(solid | 0xFF000000)) : 0);
            views.SetInt(bg, "setImageAlpha", settings.WidgetLook.Alpha);
            var image = R.Id(context, "goal_widget_image");
            var off = R.Id(context, "goal_widget_off");
            views.SetTextColor(off, new Color(unchecked((int)secondary)));

            var showing = enabled && model is not null;
            views.SetViewVisibility(image, showing ? ViewStates.Visible : ViewStates.Gone);
            views.SetViewVisibility(off, showing ? ViewStates.Gone : ViewStates.Visible);
            views.SetTextViewText(off, !enabled ? "Wallet is turned off. Open Helm to turn it on."
                : model is null ? "Nothing to save for yet. Tap to start a savings goal in Wallet." : "");
            if (showing)
            {
                var density = context.Resources?.DisplayMetrics?.Density ?? 2f;
                var scale = Math.Min(density, MaxPixels / Math.Max(width, height));
                var photo = Photo(context, book!, model!);
                views.SetImageViewBitmap(image, Draw(model!, photo, width, height, scale, night, main, secondary));
                views.SetContentDescription(image, model!.Description);
            }
            if (Open(context, widgetId, model) is { } open) views.SetOnClickPendingIntent(R.Id(context, "goal_widget_root"), open);
            manager.UpdateAppWidget(widgetId, views);
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not draw the Goal widget");
        }
    }

    /// <summary>
    /// The widget as one picture: the photo (rounded, lit up to the share) with the percentage on it; when wide, the
    /// name, the money, a bar and "+ Set aside" beside it.
    /// </summary>
    private static Bitmap Draw(GoalWidgetModel model, Bitmap? photo, float widthDp, float heightDp, float scale, bool night, uint main, uint secondary)
    {
        var w = Math.Max(1, (int)(widthDp * scale));
        var h = Math.Max(1, (int)(heightDp * scale));
        var bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        using var paint = new Paint(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
        var wide = widthDp >= 230;
        var radius = 16 * scale;
        var picture = wide ? new RectF(0, 0, Math.Min(w * 0.48f, h * 1.25f), h) : new RectF(0, 0, w, h);

        using (var clip = new Android.Graphics.Path())
        {
            clip.AddRoundRect(picture, radius, radius, Android.Graphics.Path.Direction.Cw!);
            canvas.Save();
            canvas.ClipPath(clip);
            var line = picture.Bottom - (float)model.Share * picture.Height();
            if (photo is not null)
            {
                var source = Cover(photo, picture);
                paint.Color = Color.Black;
                canvas.DrawRect(picture, paint);
                // Above the line: grey and dim.
                using (var grey = new ColorMatrix())
                {
                    grey.SetSaturation(0);
                    paint.SetColorFilter(new ColorMatrixColorFilter(grey));
                    paint.Alpha = 128;
                    canvas.DrawBitmap(photo, source, picture, paint);
                    paint.SetColorFilter(null);
                    paint.Alpha = 255;
                }
                canvas.Save();
                canvas.ClipRect(picture.Left, line, picture.Right, picture.Bottom);
                canvas.DrawBitmap(photo, source, picture, paint);
                canvas.Restore();
            }
            else
            {
                var color = WalletPalette.Color(model.Color, night);
                paint.Color = new Color(unchecked((int)WalletPalette.WithAlpha(color, 0x40)));
                canvas.DrawRect(picture, paint);
                paint.Color = new Color(unchecked((int)color));
                canvas.DrawRect(picture.Left, line, picture.Right, picture.Bottom, paint);
                paint.Color = Color.White;
                paint.TextAlign = Paint.Align.Center;
                paint.SetTypeface(Typeface.Create(Typeface.SansSerif, TypefaceStyle.Bold));
                paint.TextSize = Math.Min(picture.Width(), picture.Height()) * 0.4f;
                canvas.DrawText(model.Name.Length > 0 ? model.Name[..1].ToUpperInvariant() : "?", picture.CenterX(), picture.CenterY() + paint.TextSize * 0.35f, paint);
            }
            if (model.Share is > 0.002 and < 0.998)
            {
                // The bright edge of the light: a soft glow and a thin line.
                using var glow = new LinearGradient(0, line - 10 * scale, 0, line, new Color(0x00FFFFFF), new Color(0x70FFFFFF), Shader.TileMode.Clamp!);
                paint.SetShader(glow);
                canvas.DrawRect(picture.Left, line - 10 * scale, picture.Right, line, paint);
                paint.SetShader(null);
                paint.Color = new Color(unchecked((int)0xE6FFFFFFu));
                canvas.DrawRect(picture.Left, line - 1 * scale, picture.Right, line + 1 * scale, paint);
            }
            // Words on the photo: white with a shadow.
            paint.TextAlign = Paint.Align.Left;
            paint.SetShadowLayer(6 * scale, 0, 0, new Color(unchecked((int)0xCC000000u)));
            paint.Color = Color.White;
            paint.SetTypeface(Typeface.Create(Typeface.SansSerif, TypefaceStyle.Bold));
            paint.TextSize = (wide ? 26 : 28) * scale;
            canvas.DrawText(model.PercentText, picture.Left + 12 * scale, picture.Bottom - 12 * scale, paint);
            if (!wide)
            {
                paint.TextSize = 13 * scale;
                paint.SetTypeface(Typeface.Create(Typeface.SansSerif, TypefaceStyle.Normal));
                canvas.DrawText(Fit(paint, model.Name, picture.Width() - 24 * scale), picture.Left + 12 * scale, picture.Top + 22 * scale, paint);
            }
            paint.ClearShadowLayer();
            canvas.Restore();
        }
        if (model.IsReady)
        {
            paint.SetStyle(Paint.Style.Stroke);
            paint.StrokeWidth = 3 * scale;
            paint.Color = new Color(unchecked((int)0xFFFFC83Du));
            var inset = 1.5f * scale;
            canvas.DrawRoundRect(picture.Left + inset, picture.Top + inset, picture.Right - inset, picture.Bottom - inset, radius - inset, radius - inset, paint);
            paint.SetStyle(Paint.Style.Fill);
        }
        if (!wide) return bitmap;

        // Beside the photo: the name, the money, a bar, what is left, and the button.
        var x = picture.Right + 14 * scale;
        var room = w - x - 14 * scale;
        var y = 26 * scale;
        paint.TextAlign = Paint.Align.Left;
        paint.SetTypeface(Typeface.Create(Typeface.SansSerif, TypefaceStyle.Bold));
        paint.TextSize = 15 * scale;
        paint.Color = new Color(unchecked((int)main));
        canvas.DrawText(Fit(paint, model.Name, room), x, y, paint);
        y += 24 * scale;
        paint.TextSize = 18 * scale;
        canvas.DrawText(Fit(paint, model.SavedShort, room), x, y, paint);
        var savedWidth = paint.MeasureText(model.SavedShort);
        paint.SetTypeface(Typeface.Create(Typeface.SansSerif, TypefaceStyle.Normal));
        paint.TextSize = 12.5f * scale;
        paint.Color = new Color(unchecked((int)secondary));
        if (savedWidth + 8 * scale < room) canvas.DrawText(Fit(paint, " / " + model.PriceShort, room - savedWidth), x + savedWidth, y, paint);
        y += 12 * scale;
        var bar = new RectF(x, y, x + room, y + 5 * scale);
        paint.Color = new Color(unchecked((int)(night ? 0x33FFFFFFu : 0x1F000000u)));
        canvas.DrawRoundRect(bar, 3 * scale, 3 * scale, paint);
        paint.Color = new Color(unchecked((int)(model.IsReady ? 0xFFE0A800u : WalletWidgetModel.ColorOf(WalletWidgetModel.BalanceColor, night))));
        canvas.DrawRoundRect(bar.Left, bar.Top, bar.Left + Math.Max(bar.Height(), (float)model.Share * bar.Width()), bar.Bottom, 3 * scale, 3 * scale, paint);
        y += 24 * scale;
        paint.Color = new Color(unchecked((int)secondary));
        canvas.DrawText(Fit(paint, model.LeftShort, room), x, y, paint);
        if (model.PaceShort.Length > 0 && y + 18 * scale < h - 40 * scale)
        {
            y += 17 * scale;
            canvas.DrawText(Fit(paint, model.PaceShort, room), x, y, paint);
        }
        // The button: a pill at the bottom.
        var label = model.IsReady ? "I bought it" : "+ Set aside";
        paint.SetTypeface(Typeface.Create(Typeface.SansSerif, TypefaceStyle.Bold));
        paint.TextSize = 12.5f * scale;
        var pillWidth = Math.Min(room, paint.MeasureText(label) + 24 * scale);
        var pill = new RectF(x, h - 14 * scale - 30 * scale, x + pillWidth, h - 14 * scale);
        paint.Color = new Color(unchecked((int)(model.IsReady ? 0xFFFFC83Du : WalletWidgetModel.ColorOf(WalletWidgetModel.BalanceColor, night))));
        canvas.DrawRoundRect(pill, 15 * scale, 15 * scale, paint);
        paint.Color = model.IsReady ? new Color(unchecked((int)0xFF2A1C00u)) : night ? new Color(unchecked((int)0xFF00231Cu)) : Color.White;
        paint.TextAlign = Paint.Align.Center;
        canvas.DrawText(label, pill.CenterX(), pill.CenterY() + paint.TextSize * 0.35f, paint);
        return bitmap;
    }

    /// <summary>The part of the photo that fills the box, centred (the rest is cut off).</summary>
    private static Rect Cover(Bitmap photo, RectF box)
    {
        var scale = Math.Max(box.Width() / photo.Width, box.Height() / photo.Height);
        var width = (int)(box.Width() / scale);
        var height = (int)(box.Height() / scale);
        var left = (photo.Width - width) / 2;
        var top = (photo.Height - height) / 2;
        return new Rect(left, top, left + width, top + height);
    }

    /// <summary>The text, cut with an ellipsis when it is wider than the room.</summary>
    private static string Fit(Paint paint, string text, float room)
    {
        if (paint.MeasureText(text) <= room) return text;
        for (var n = text.Length - 1; n > 1; n--)
        {
            var cut = text[..n].TrimEnd() + "…";
            if (paint.MeasureText(cut) <= room) return cut;
        }
        return text.Length > 0 ? text[..1] : "";
    }

    /// <summary>The goal's photo when it was read before; else it is read in the background and the widgets redrawn.</summary>
    private static Bitmap? Photo(Context context, GoalBook book, GoalWidgetModel model)
    {
        if (model.Photo is not { } blob) return null;
        lock (s_gate)
        {
            if (s_photos.TryGetValue(blob.Id, out var known)) return known;
            if (!s_reading.Add(blob.Id)) return null;
        }
        var app = context.ApplicationContext ?? context;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await book.ReadPhotoAsync(blob).ConfigureAwait(false) is not { } bytes) return;
                if (BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length) is not { } decoded) return;
                lock (s_gate)
                {
                    // One goal at a time is on the widgets: older photos go.
                    foreach (var old in s_photos.Values) old.Recycle();
                    s_photos.Clear();
                    s_photos[blob.Id] = decoded;
                }
                RefreshAll(app);
            }
            catch (Exception ex)
            {
                WalletNotifications.Log(ex, "Could not read the Goal widget's photo");
            }
            finally
            {
                lock (s_gate) s_reading.Remove(blob.Id);
            }
        });
        return null;
    }

    /// <summary>A tap opens Wallet on the goal (or on the goals, to start one).</summary>
    private static PendingIntent? Open(Context context, int widgetId, GoalWidgetModel? model)
    {
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launch is null) return null;
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, WalletIds.ModuleId);
        launch.PutExtra(ShellIntents.ExtraItem, ItemPrefix + (model?.Id ?? ""));
        return PendingIntent.GetActivity(context, 0x6A00 + widgetId, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private static (GoalWidgetModel? Model, WalletSettings Settings, bool Enabled, GoalBook? Book) Load()
    {
        var settings = new WalletSettings();
        try
        {
            var services = HelmAndroidServices.Current;
            var store = services.GetRequiredService<WalletStore>();
            var capture = services.GetRequiredService<WalletCapture>();
            var book = services.GetRequiredService<GoalBook>();
            settings = capture.Settings.Current;
            return (GoalWidgetModel.Build(book, settings.WidgetGoalId, store.Now, TimeZoneInfo.Local), settings, capture.IsToolEnabled, book);
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not load the Goal widget data");
            return (null, settings, true, null);
        }
    }

    /// <summary>The widget's size in dp, as the launcher reports it for portrait.</summary>
    private static (float Width, float Height) SizeOf(AppWidgetManager manager, int widgetId)
    {
        var options = manager.GetAppWidgetOptions(widgetId);
        var width = options?.GetInt(AppWidgetManager.OptionAppwidgetMinWidth) ?? 0;
        var height = options?.GetInt(AppWidgetManager.OptionAppwidgetMaxHeight) ?? 0;
        return (width > 0 ? width : 160, height > 0 ? height : 160);
    }
}
