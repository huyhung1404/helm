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
using Path = Android.Graphics.Path;
using RemoteViews = Android.Widget.RemoteViews;
using ViewStates = Android.Views.ViewStates;

namespace Helm.Modules.Wallet;

/// <summary>The "Funds" home-screen widget: the month's funds as honey pots, the ones that need a look first.</summary>
[BroadcastReceiver(Label = "Funds", Exported = false)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/funds_widget_info")]
public sealed class FundsWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        foreach (var id in appWidgetIds) FundsWidgets.Update(context, appWidgetManager, id);
    }

    /// <summary>Resized: as many jars as fit.</summary>
    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? appWidgetManager, int appWidgetId, Bundle? newOptions)
    {
        if (context is null || appWidgetManager is null) return;
        FundsWidgets.Update(context, appWidgetManager, appWidgetId);
    }
}

/// <summary>
/// Draws the Funds widgets. The jars are one bitmap (a widget cannot clip to a pot's shape), drawn again at each update,
/// so the water stands still and drops when a transaction comes off a fund. Runs in the app process, possibly without
/// the activity, so services come from <see cref="HelmAndroidServices"/>; the data is <see cref="FundsWidgetModel"/>.
/// </summary>
internal static class FundsWidgets
{
    // The largest bitmap, in pixels: sharp at any size, still small for the launcher (widget bitmaps are limited).
    private const int MaxPixels = 900;

    public static void RefreshAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        var ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(FundsWidgetProvider))));
        if (ids is not { Length: > 0 }) return;
        foreach (var id in ids) Update(context, manager, id);
    }

    public static void Update(Context context, AppWidgetManager manager, int widgetId)
    {
        try
        {
            var (width, height) = SizeOf(manager, widgetId);
            var shown = width < 220 ? 2 : Math.Clamp((int)((width - 28) / 78), 3, 6);
            var (model, settings, enabled) = Load(shown);
            var views = new RemoteViews(context.PackageName, R.Layout(context, "funds_widget"));
            var night = ((context.Resources?.Configuration?.UiMode ?? UiMode.NightNo) & UiMode.NightMask) == UiMode.NightYes;
            var dark = settings.WidgetLook.LightText ?? night;
            var (main, secondary) = settings.WidgetLook.TextColors ?? (night ? (0xFFFFFFFFu, 0xFFC5C5C5u) : (0xFF1A1A1Au, 0xFF5F5F5Fu));

            var bg = R.Id(context, "funds_widget_bg");
            views.SetInt(bg, "setColorFilter", settings.WidgetLook.BackgroundColor is { } solid ? unchecked((int)(solid | 0xFF000000)) : 0);
            views.SetInt(bg, "setImageAlpha", settings.WidgetLook.Alpha);

            var title = R.Id(context, "funds_widget_title");
            var left = R.Id(context, "funds_widget_left");
            var jars = R.Id(context, "funds_widget_jars");
            var off = R.Id(context, "funds_widget_off");
            var footer = R.Id(context, "funds_widget_footer");
            views.SetTextColor(title, new Color(unchecked((int)secondary)));
            views.SetTextColor(left, new Color(unchecked((int)main)));
            views.SetTextColor(off, new Color(unchecked((int)secondary)));
            views.SetTextColor(footer, new Color(unchecked((int)secondary)));
            views.SetInt(R.Id(context, "funds_widget_icon"), "setColorFilter", unchecked((int)WalletWidgetModel.ColorOf(WalletWidgetModel.BalanceColor, dark)));

            var showing = enabled && model is { IsEmpty: false };
            views.SetViewVisibility(jars, showing ? ViewStates.Visible : ViewStates.Gone);
            views.SetViewVisibility(off, showing ? ViewStates.Gone : ViewStates.Visible);
            views.SetTextViewText(off, !enabled ? "Wallet is turned off. Open Helm to turn it on." : model is null ? "Open Helm to load your funds." : model.Footer);
            views.SetTextViewText(title, model?.Title ?? "Funds");
            views.SetTextViewText(left, showing ? model!.LeftText : "");
            views.SetTextViewText(footer, showing ? model!.Footer : "");
            views.SetViewVisibility(footer, showing && model!.Footer.Length > 0 ? ViewStates.Visible : ViewStates.Gone);
            if (showing)
            {
                var density = context.Resources?.DisplayMetrics?.Density ?? 2f;
                var w = Math.Clamp((int)((width - 28) * density), 120, MaxPixels);
                var h = Math.Clamp((int)((height - 12 - 10 - 20 - 6 - 18) * density), 90, MaxPixels);
                views.SetImageViewBitmap(jars, JarsBitmap(model!, w, h, density, dark, main));
                views.SetContentDescription(jars, string.Join(". ", model!.Jars.Select(j => $"{j.Name}: {j.AmountText}")));
            }
            if (WalletNotifications.OpenWalletIntent(context, 0x5B20 + widgetId) is { } open)
                views.SetOnClickPendingIntent(R.Id(context, "funds_widget_root"), open);
            manager.UpdateAppWidget(widgetId, views);
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not draw the Funds widget");
        }
    }

    /// <summary>The jars side by side, each with its name and what is left (or over) under it.</summary>
    private static Bitmap JarsBitmap(FundsWidgetModel model, int width, int height, float density, bool dark, uint main)
    {
        var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        using var paint = new Paint(PaintFlags.AntiAlias);
        var columns = model.Jars.Count;
        var cell = width / (float)columns;
        var nameSize = 11 * density;
        var amountSize = 11.5f * density;
        var textRoom = nameSize + amountSize + 8 * density;
        var jarHeight = Math.Max(24 * density, height - textRoom);
        var jarWidth = Math.Min(cell * 0.8f, jarHeight * 100 / 110);
        jarHeight = jarWidth * 110 / 100;
        var top = Math.Max(0, (height - textRoom - jarHeight) / 2);
        for (var i = 0; i < columns; i++)
        {
            var jar = model.Jars[i];
            var x = cell * i + (cell - jarWidth) / 2;
            DrawJar(canvas, paint, jar, x, top, jarWidth / 100, dark);

            paint.SetStyle(Paint.Style.Fill);
            paint.TextAlign = Paint.Align.Center;
            var centre = cell * i + cell / 2;
            paint.SetTypeface(Typeface.Create(Typeface.SansSerif, TypefaceStyle.Bold));
            paint.TextSize = nameSize;
            paint.Color = new Color(unchecked((int)main));
            var y = top + jarHeight + nameSize + 3 * density;
            canvas.DrawText(Fit(paint, jar.Name, cell - 4 * density), centre, y, paint);
            paint.TextSize = amountSize;
            paint.Color = new Color(unchecked((int)StatusColor(jar.Status, dark, main)));
            canvas.DrawText(Fit(paint, jar.AmountText, cell - 4 * density), centre, y + amountSize + 3 * density, paint);
        }
        return bitmap;
    }

    /// <summary>The honey pot of the pages (a 100 × 110 box), scaled: lid, glass, water with a still wave, outline.</summary>
    private static void DrawJar(Canvas canvas, Paint paint, FundsWidgetJar jar, float x, float y, float scale, bool dark)
    {
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(scale, scale);
        using var body = new Path();
        body.MoveTo(50, 10);
        body.CubicTo(79, 10, 98, 23, 98, 53);
        body.CubicTo(98, 87, 79, 108, 50, 108);
        body.CubicTo(21, 108, 2, 87, 2, 53);
        body.CubicTo(2, 23, 21, 10, 50, 10);
        body.Close();

        paint.SetStyle(Paint.Style.Fill);
        paint.Color = new Color(unchecked((int)(dark ? 0x3DFFFFFFu : 0x2E000000u)));
        canvas.DrawRoundRect(32, 0, 68, 9, 2, 2, paint);
        paint.Color = new Color(unchecked((int)(dark ? 0x0FFFFFFFu : 0x08000000u)));
        canvas.DrawPath(body, paint);

        if (jar.Level > 0.001)
        {
            var top = (float)(108 - jar.Level * 98);
            paint.Color = new Color(unchecked((int)WalletPalette.WithAlpha(WalletPalette.Color(jar.Color, dark), 0xD9)));
            canvas.Save();
            canvas.ClipPath(body);
            using var water = new Path();
            water.MoveTo(0, top);
            for (var wx = 0f; wx < 100; wx += 25) water.QuadTo(wx + 12.5f, top + ((int)(wx / 25) % 2 == 0 ? -4 : 4), wx + 25, top);
            water.LineTo(100, 110);
            water.LineTo(0, 110);
            water.Close();
            canvas.DrawPath(water, paint);
            canvas.Restore();
        }
        paint.Color = new Color(0x22FFFFFF);
        canvas.DrawRoundRect(15, 30, 23, 70, 4, 4, paint);

        paint.SetStyle(Paint.Style.Stroke);
        paint.StrokeWidth = 3;
        paint.Color = new Color(unchecked((int)(jar.Status switch
        {
            FundStatus.Over => dark ? 0xFFFF99A4u : 0xFFC42B1Cu,
            FundStatus.UsedUp => dark ? 0xFFFFB86Bu : 0xFFA8461Au,
            FundStatus.Low => dark ? 0xFFFCE100u : 0xFF9D5D00u,
            _ => dark ? 0x52FFFFFFu : 0x40000000u,
        })));
        canvas.DrawPath(body, paint);
        canvas.Restore();
    }

    private static uint StatusColor(FundStatus status, bool dark, uint main) => status switch
    {
        FundStatus.Over => dark ? 0xFFFF99A4u : 0xFFC42B1Cu,
        FundStatus.UsedUp => dark ? 0xFFFFB86Bu : 0xFFA8461Au,
        FundStatus.Low => dark ? 0xFFFCE100u : 0xFF8A5300u,
        _ => main,
    };

    /// <summary>The text, cut with an ellipsis when it is wider than the room.</summary>
    private static string Fit(Paint paint, string text, float room)
    {
        if (paint.MeasureText(text) <= room) return text;
        for (var n = text.Length - 1; n > 1; n--)
        {
            var cut = text[..n].TrimEnd() + "…";
            if (paint.MeasureText(cut) <= room) return cut;
        }
        return text[..1];
    }

    private static (FundsWidgetModel? Model, WalletSettings Settings, bool Enabled) Load(int shown)
    {
        var settings = new WalletSettings();
        try
        {
            var services = HelmAndroidServices.Current;
            var store = services.GetRequiredService<WalletStore>();
            var capture = services.GetRequiredService<WalletCapture>();
            settings = capture.Settings.Current;
            var model = FundsWidgetModel.Build(services.GetRequiredService<FundBook>(), store, shown, store.Now, TimeZoneInfo.Local);
            return (model, settings, capture.IsToolEnabled);
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not load the Funds widget data");
            return (null, settings, true);
        }
    }

    /// <summary>The widget's size in dp, as the launcher reports it for portrait.</summary>
    private static (float Width, float Height) SizeOf(AppWidgetManager manager, int widgetId)
    {
        var options = manager.GetAppWidgetOptions(widgetId);
        var width = options?.GetInt(AppWidgetManager.OptionAppwidgetMinWidth) ?? 0;
        var height = options?.GetInt(AppWidgetManager.OptionAppwidgetMaxHeight) ?? 0;
        return (width > 0 ? width : 320, height > 0 ? height : 150);
    }
}
