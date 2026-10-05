using Android.Appwidget;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Color = Android.Graphics.Color;
using ComplexUnitType = Android.Util.ComplexUnitType;
using IntentFilterAttribute = Android.App.IntentFilterAttribute;
using MetaDataAttribute = Android.App.MetaDataAttribute;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;
using RemoteViews = Android.Widget.RemoteViews;
using ViewStates = Android.Views.ViewStates;

namespace Helm.Modules.Wallet;

/// <summary>The "Wallet" home-screen widget: the balance in a ring, with the spending and income of a chosen period.</summary>
[BroadcastReceiver(Label = "Wallet", Exported = false)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/wallet_widget_info")]
public sealed class WalletWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        foreach (var id in appWidgetIds) WalletWidgets.Update(context, appWidgetManager, id);
    }

    /// <summary>Resized: the layout (small or wide), the ring and the number of rows follow the new size.</summary>
    public override void OnAppWidgetOptionsChanged(Context? context, AppWidgetManager? appWidgetManager, int appWidgetId, Bundle? newOptions)
    {
        if (context is null || appWidgetManager is null) return;
        WalletWidgets.Update(context, appWidgetManager, appWidgetId);
    }

    public override void OnDeleted(Context? context, int[]? appWidgetIds)
    {
        if (context is null || appWidgetIds is null) return;
        foreach (var id in appWidgetIds) WalletWidgets.Forget(context, id);
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        base.OnReceive(context, intent);
        if (context is null || intent?.Action != WalletWidgets.ActionPeriod) return;
        var id = intent.GetIntExtra(AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId);
        if (id != AppWidgetManager.InvalidAppwidgetId) WalletWidgets.SetPeriod(context, id, intent.GetIntExtra(WalletWidgets.ExtraPeriod, -1));
    }
}

/// <summary>
/// Draws the Wallet widgets. The ring is a bitmap (a widget cannot draw arcs); the text around it is ordinary text
/// views. Each widget remembers its own period. A tap on a tab switches the period; anywhere else (the red dot too,
/// whose transactions head the Wallet page) opens Helm on Wallet. Runs in the app process, possibly without the
/// activity, so services come from <see cref="HelmAndroidServices"/>. The data comes from <see cref="WalletWidgetModel"/>
/// (tested in the core).
/// </summary>
internal static class WalletWidgets
{
    public const string ActionPeriod = "com.huyhung1404.helm.wallet.WIDGET_PERIOD";
    public const string ExtraPeriod = "com.huyhung1404.helm.wallet.extra.PERIOD";

    private const string PreferencesName = "helm_wallet_widgets";
    private const WalletWidgetPeriod DefaultPeriod = WalletWidgetPeriod.Month;

    // The ring, as fractions of its square: the radius of the stroke's centre line and the stroke's width.
    private const float RingRadius = 0.42f;
    private const float RingWidth = 0.095f;

    // The largest ring bitmap, in pixels: plenty for a sharp ring and small for the launcher (widget bitmaps are limited).
    private const int MaxRingPixels = 480;

    private static readonly WalletWidgetPeriod[] s_periods = Enum.GetValues<WalletWidgetPeriod>();

    public static void RefreshAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        var ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(WalletWidgetProvider))));
        if (ids is not { Length: > 0 }) return;
        foreach (var id in ids) Update(context, manager, id);
    }

    /// <summary>Whether the launcher can place a widget when the app asks (Android 8+, most launchers).</summary>
    public static bool CanRequestPin(Context context)
    {
        try { return AppWidgetManager.GetInstance(context)?.IsRequestPinAppWidgetSupported == true; }
        catch (Exception) { return false; }
    }

    /// <summary>Asks the launcher to add the Wallet widget; the launcher shows its own confirmation.</summary>
    public static void RequestPin(Context context)
    {
        try
        {
            var provider = new ComponentName(context, Java.Lang.Class.FromType(typeof(WalletWidgetProvider)));
            AppWidgetManager.GetInstance(context)?.RequestPinAppWidget(provider, null, null);
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not ask the launcher to add the Wallet widget");
        }
    }

    /// <summary>A tab (or the small widget's button): remember the period for this widget and redraw it.</summary>
    public static void SetPeriod(Context context, int widgetId, int period)
    {
        if (!Enum.IsDefined((WalletWidgetPeriod)period)) return;
        Preferences(context).Edit()?.PutInt(PeriodKey(widgetId), period)?.Apply();
        if (AppWidgetManager.GetInstance(context) is { } manager) Update(context, manager, widgetId);
    }

    public static void Forget(Context context, int widgetId) => Preferences(context).Edit()?.Remove(PeriodKey(widgetId))?.Apply();

    public static void Update(Context context, AppWidgetManager manager, int widgetId)
    {
        try
        {
            var size = Size.Of(manager, widgetId);
            var period = GetPeriod(context, widgetId);
            var (model, settings, enabled) = Load(period);
            var views = new RemoteViews(context.PackageName, R.Layout(context, size.Small ? "wallet_widget_small" : "wallet_widget"));
            var colours = Colours.For(context, settings.WidgetLook);

            var bg = R.Id(context, "wallet_widget_bg");
            views.SetInt(bg, "setColorFilter", settings.WidgetLook.BackgroundColor is { } solid ? unchecked((int)(solid | 0xFF000000)) : 0);
            views.SetInt(bg, "setImageAlpha", settings.WidgetLook.Alpha);

            var off = R.Id(context, "wallet_widget_off");
            var showing = enabled && model is not null;
            views.SetViewVisibility(off, showing ? ViewStates.Gone : ViewStates.Visible);
            views.SetViewVisibility(R.Id(context, "wallet_widget_body"), showing ? ViewStates.Visible : ViewStates.Gone);
            views.SetTextViewText(off, !enabled ? "Wallet is turned off. Open Helm to turn it on." : "Open Helm to load your wallet.");
            views.SetTextColor(off, colours.Secondary);

            if (showing)
            {
                DrawRing(context, views, model!, size, colours, settings.WidgetHideBalance);
                if (size.Small) DrawSmall(context, views, widgetId, model!, colours);
                else DrawWide(context, views, widgetId, model!, size, colours, settings.WidgetHideBalance);
            }
            else if (size.Small)
            {
                views.SetViewVisibility(R.Id(context, "wallet_widget_cycle"), ViewStates.Gone);
            }
            else
            {
                views.SetViewVisibility(R.Id(context, "wallet_widget_header"), ViewStates.Gone);
                views.SetViewVisibility(R.Id(context, "wallet_widget_toptabs"), ViewStates.Gone);
            }

            if (WalletNotifications.OpenWalletIntent(context, 0x5A20 + widgetId) is { } open)
                views.SetOnClickPendingIntent(R.Id(context, "wallet_widget_root"), open);
            manager.UpdateAppWidget(widgetId, views);
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not draw the Wallet widget");
        }
    }

    // ---- Parts ---------------------------------------------------------------------------------------------------

    /// <summary>The ring bitmap, the words in its middle (the full amount when it fits, else the short one) and the red dot.</summary>
    private static void DrawRing(Context context, RemoteViews views, WalletWidgetModel model, Size size, Colours colours, bool hideBalance)
    {
        var density = context.Resources?.DisplayMetrics?.Density ?? 2f;
        var pixels = Math.Clamp((int)(size.RingDp * density), 64, MaxRingPixels);
        var ring = R.Id(context, "wallet_widget_ring");
        views.SetImageViewBitmap(ring, RingBitmap(model, pixels, colours));
        views.SetContentDescription(ring, Describe(model, hideBalance));

        var label = R.Id(context, "wallet_widget_center_label");
        views.SetTextViewText(label, model.CenterLabel(wide: size.Tall));
        views.SetTextColor(label, colours.Secondary);

        var hidden = hideBalance && model.HasBalance;
        var full = hidden ? "••••••" : WalletFormat.Money(model.CenterAmount);
        var brief = hidden ? "••••••" : WalletFormat.Short(model.CenterAmount);
        var (text, sp) = FitCenter(context, full, brief, size.RingDp * (RingRadius - RingWidth / 2) * 2 * 0.86f, size.Tall ? 22 : 19);
        var value = R.Id(context, "wallet_widget_center_value");
        views.SetTextViewText(value, text);
        views.SetTextViewTextSize(value, (int)ComplexUnitType.Sp, sp);
        views.SetTextColor(value, colours.Main);

        var badge = R.Id(context, "wallet_widget_badge");
        views.SetViewVisibility(badge, model.BadgeText is null ? ViewStates.Gone : ViewStates.Visible);
        if (model.BadgeText is { } count)
        {
            views.SetTextViewText(badge, count);
            views.SetContentDescription(badge, WalletFormat.Count(model.ToCategorize, "transaction") + " to categorize");
        }
    }

    /// <summary>About 2 × 2: one button steps through the periods; the spending and income sit under the balance.</summary>
    private static void DrawSmall(Context context, RemoteViews views, int widgetId, WalletWidgetModel model, Colours colours)
    {
        var cycle = R.Id(context, "wallet_widget_cycle");
        views.SetViewVisibility(cycle, ViewStates.Visible);
        views.SetTextViewText(cycle, model.PeriodName + " ›");
        views.SetTextColor(cycle, colours.Main);
        views.SetInt(cycle, "setBackgroundResource", colours.Pill(context));
        views.SetContentDescription(cycle, $"{model.PeriodName}. Tap for {WalletWidgetModel.TabName(WalletWidgetModel.Next(model.Period))}");
        views.SetOnClickPendingIntent(cycle, PeriodIntent(context, widgetId, WalletWidgetModel.Next(model.Period)));

        var detail = R.Id(context, "wallet_widget_center_detail");
        var text = model.CenterDetail;
        views.SetViewVisibility(detail, text.Length > 0 ? ViewStates.Visible : ViewStates.Gone);
        views.SetTextViewText(detail, text);
        views.SetTextColor(detail, colours.Secondary);
    }

    /// <summary>
    /// Four columns: tabs, spent and earned, and the largest slices beside the ring. Tall widgets add a title, put the
    /// tabs with their full names across the top, show each slice's share and write the amounts in full.
    /// </summary>
    private static void DrawWide(Context context, RemoteViews views, int widgetId, WalletWidgetModel model, Size size, Colours colours, bool hideBalance)
    {
        views.SetViewVisibility(R.Id(context, "wallet_widget_header"), size.Tall ? ViewStates.Visible : ViewStates.Gone);
        views.SetTextColor(R.Id(context, "wallet_widget_title"), colours.Secondary);
        views.SetInt(R.Id(context, "wallet_widget_icon"), "setColorFilter", unchecked((int)colours.Brand));
        views.SetViewVisibility(R.Id(context, "wallet_widget_toptabs"), size.Tall ? ViewStates.Visible : ViewStates.Gone);
        views.SetViewVisibility(R.Id(context, "wallet_widget_sidetabs"), size.Tall ? ViewStates.Gone : ViewStates.Visible);
        var prefix = size.Tall ? "wallet_widget_toptab" : "wallet_widget_sidetab";
        for (var i = 0; i < s_periods.Length; i++)
        {
            var period = s_periods[i];
            var tab = R.Id(context, prefix + i);
            var chosen = period == model.Period;
            views.SetTextViewText(tab, size.Tall ? WalletWidgetModel.TabName(period) : WalletWidgetModel.ShortTabName(period));
            views.SetTextColor(tab, chosen ? colours.Main : colours.Secondary);
            views.SetInt(tab, "setBackgroundResource", chosen ? colours.Pill(context) : 0);
            views.SetContentDescription(tab, WalletWidgetModel.TabName(period) + (chosen ? ", shown" : ""));
            views.SetOnClickPendingIntent(tab, PeriodIntent(context, widgetId, period));
        }

        Func<decimal, string> amount = size.Tall ? a => WalletFormat.Money(a) : a => WalletFormat.Short(a);
        Total(context, views, "spent", model.Spent, "−", amount, colours);
        Total(context, views, "earned", model.Earned, "+", amount, colours);

        var empty = R.Id(context, "wallet_widget_empty");
        views.SetViewVisibility(empty, model.IsEmpty ? ViewStates.Visible : ViewStates.Gone);
        views.SetTextViewText(empty, model.EmptyText);
        views.SetTextColor(empty, colours.Secondary);

        var rows = Math.Min(size.LegendRows, model.Slices.Count);
        for (var i = 0; i < 5; i++)
        {
            var n = i + 1;
            views.SetViewVisibility(R.Id(context, $"wallet_widget_row{n}"), i < rows ? ViewStates.Visible : ViewStates.Gone);
            if (i >= rows) continue;
            var slice = model.Slices[i];
            views.SetInt(R.Id(context, $"wallet_widget_dot{n}"), "setColorFilter", unchecked((int)WalletWidgetModel.ColorOf(slice.Color, colours.Dark)));
            var name = R.Id(context, $"wallet_widget_name{n}");
            views.SetTextViewText(name, slice.Name);
            views.SetTextColor(name, colours.Main);
            var value = R.Id(context, $"wallet_widget_amount{n}");
            views.SetTextViewText(value, hideBalance && slice.Color == WalletWidgetModel.BalanceColor ? "••••" : amount(slice.Amount));
            views.SetTextColor(value, colours.Main);
            var share = R.Id(context, $"wallet_widget_share{n}");
            views.SetViewVisibility(share, size.Tall ? ViewStates.Visible : ViewStates.Gone);
            views.SetTextViewText(share, slice.ShareText);
            views.SetTextColor(share, colours.Secondary);
        }
        var more = R.Id(context, "wallet_widget_more");
        var hiddenRows = model.Slices.Count - rows;
        views.SetViewVisibility(more, hiddenRows > 0 && rows > 0 ? ViewStates.Visible : ViewStates.Gone);
        views.SetTextViewText(more, $"+{hiddenRows} more");
        views.SetTextColor(more, colours.Secondary);
    }

    /// <summary>"Spent −1.7M ₫" / "Earned +500K ₫"; a zero is written plainly in the secondary colour.</summary>
    private static void Total(Context context, RemoteViews views, string name, decimal value, string sign, Func<decimal, string> format, Colours colours)
    {
        views.SetTextColor(R.Id(context, $"wallet_widget_{name}_label"), colours.Secondary);
        var id = R.Id(context, $"wallet_widget_{name}");
        views.SetTextViewText(id, value > 0 ? sign + format(value) : format(0));
        views.SetTextColor(id, value > 0 ? colours.Main : colours.Secondary);
    }

    /// <summary>The ring: a faint track, then each slice clockwise from the top with a thin gap between slices.</summary>
    private static Bitmap RingBitmap(WalletWidgetModel model, int pixels, Colours colours)
    {
        var bitmap = Bitmap.CreateBitmap(pixels, pixels, Bitmap.Config.Argb8888!)!;
        using var canvas = new Canvas(bitmap);
        using var paint = new Paint(PaintFlags.AntiAlias);
        paint.SetStyle(Paint.Style.Stroke);
        paint.StrokeCap = Paint.Cap.Butt;
        paint.StrokeWidth = pixels * RingWidth;
        var centre = pixels / 2f;
        var radius = pixels * RingRadius;
        using var oval = new RectF(centre - radius, centre - radius, centre + radius, centre + radius);

        paint.Color = new Color(unchecked((int)colours.Track));
        canvas.DrawCircle(centre, centre, radius, paint);
        if (model.Slices.Count == 1)
        {
            paint.Color = new Color(unchecked((int)WalletWidgetModel.ColorOf(model.Slices[0].Color, colours.Dark)));
            canvas.DrawCircle(centre, centre, radius, paint);
        }
        else if (model.Slices.Count > 1)
        {
            // A gap of about 2 dp where slices meet, the surface showing through.
            var gap = (float)(1.8 / 42 * 180 / Math.PI);
            var start = -90f;
            foreach (var slice in model.Slices)
            {
                var sweep = (float)(slice.Share * 360);
                paint.Color = new Color(unchecked((int)WalletWidgetModel.ColorOf(slice.Color, colours.Dark)));
                canvas.DrawArc(oval, start + gap / 2, Math.Max(sweep - gap, 0.5f), false, paint);
                start += sweep;
            }
        }
        return bitmap;
    }

    /// <summary>What the ring says, for screen readers: the balance and each slice.</summary>
    private static string Describe(WalletWidgetModel model, bool hideBalance)
    {
        var parts = new List<string>();
        if (model.Balance is { } balance && !hideBalance) parts.Add("Balance " + WalletFormat.Money(balance));
        parts.Add($"Spent {model.PeriodWords} {WalletFormat.Money(model.Spent)}, earned {WalletFormat.Money(model.Earned)}");
        parts.AddRange(model.Slices
            .Where(s => !(hideBalance && s.Color == WalletWidgetModel.BalanceColor))
            .Select(s => $"{s.Name} {WalletFormat.Money(s.Amount)}, {s.ShareText}"));
        return string.Join(". ", parts);
    }

    /// <summary>The full amount at the largest size that fits inside the ring, else the short one (down to 12 sp).</summary>
    private static (string Text, int Sp) FitCenter(Context context, string full, string brief, float widthDp, int largest)
    {
        const int Smallest = 12;
        var fontScale = context.Resources?.Configuration?.FontScale ?? 1f;
        using var paint = new Paint(PaintFlags.AntiAlias);
        paint.SetTypeface(Typeface.DefaultBold);
        foreach (var text in new[] { full, brief })
        {
            for (var sp = largest; sp >= Smallest; sp--)
            {
                paint.TextSize = sp * fontScale; // in dp, like widthDp
                if (paint.MeasureText(text) <= widthDp) return (text, sp);
            }
        }
        return (brief, Smallest);
    }

    private static PendingIntent PeriodIntent(Context context, int widgetId, WalletWidgetPeriod period)
    {
        var intent = new Intent(context, typeof(WalletWidgetProvider)).SetAction(ActionPeriod)!;
        intent.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
        intent.PutExtra(ExtraPeriod, (int)period);
        // One request code per widget and period, so each tab keeps its own extras.
        return PendingIntent.GetBroadcast(context, widgetId * 8 + (int)period, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    // ---- Data ----------------------------------------------------------------------------------------------------

    private static (WalletWidgetModel? Model, WalletSettings Settings, bool Enabled) Load(WalletWidgetPeriod period)
    {
        var settings = new WalletSettings();
        try
        {
            var services = HelmAndroidServices.Current;
            var store = services.GetRequiredService<WalletStore>();
            var capture = services.GetRequiredService<WalletCapture>();
            settings = capture.Settings.Current;
            return (WalletWidgetModel.Build(store, period, settings.WidgetChart, store.Now, TimeZoneInfo.Local), settings, capture.IsToolEnabled);
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not load the Wallet widget data");
            return (null, settings, true);
        }
    }

    private static WalletWidgetPeriod GetPeriod(Context context, int widgetId)
    {
        var stored = Preferences(context).GetInt(PeriodKey(widgetId), (int)DefaultPeriod);
        return Enum.IsDefined((WalletWidgetPeriod)stored) ? (WalletWidgetPeriod)stored : DefaultPeriod;
    }

    private static ISharedPreferences Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)!;

    private static string PeriodKey(int widgetId) => $"period_{widgetId}";

    // ---- Size and colours ----------------------------------------------------------------------------------------

    /// <summary>
    /// What fits in the widget's size (in dp, as the launcher reports it for portrait): the small or wide layout, the
    /// ring's diameter and how many legend rows fit beside it.
    /// </summary>
    private readonly record struct Size(bool Small, bool Tall, float RingDp, int LegendRows)
    {
        public static Size Of(AppWidgetManager manager, int widgetId)
        {
            var options = manager.GetAppWidgetOptions(widgetId);
            var width = options?.GetInt(AppWidgetManager.OptionAppwidgetMinWidth) ?? 0;
            var height = options?.GetInt(AppWidgetManager.OptionAppwidgetMaxHeight) ?? 0;
            if (width <= 0) width = 320;
            if (height <= 0) height = 150;

            if (width < 220)
                return new Size(true, false, Math.Max(60, Math.Min(width - 24, height - 20 - 26)), 0);

            var tall = height >= 200;
            // Body height: the padding, and when tall the title and the tabs across the top.
            var body = height - 24 - (tall ? 22 + 30 : 0);
            var ring = Math.Max(60, Math.Min(body, (width - 28) * 0.46f));
            // Beside the ring: the tabs (short widgets), spent and earned, then rows of about 19 dp.
            var side = body - (tall ? 0 : 28) - 40;
            return new Size(false, tall, ring, Math.Clamp(side / 19, 0, 5));
        }
    }

    /// <summary>The widget's colours on its background: the settings', or the phone theme's when they follow it.</summary>
    private sealed record Colours(Color Main, Color Secondary, bool Dark, uint Track, uint Brand)
    {
        public static Colours For(Context context, WidgetLook look)
        {
            var night = ((context.Resources?.Configuration?.UiMode ?? UiMode.NightNo) & UiMode.NightMask) == UiMode.NightYes;
            var dark = look.LightText ?? night;
            var (main, secondary) = look.TextColors ?? (night ? (0xFFFFFFFFu, 0xFFC5C5C5u) : (0xFF1A1A1Au, 0xFF5F5F5Fu));
            return new Colours(new Color(unchecked((int)main)), new Color(unchecked((int)secondary)), dark,
                dark ? 0x29FFFFFFu : 0x17000000u, WalletWidgetModel.ColorOf(WalletWidgetModel.BalanceColor, dark));
        }

        /// <summary>The soft pill behind the chosen tab.</summary>
        public int Pill(Context context) => R.Drawable(context, Dark ? "wallet_widget_pill_dark" : "wallet_widget_pill_light");
    }
}
