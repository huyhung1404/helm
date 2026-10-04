using Android.Appwidget;
using Android.Content;
using Helm.Core;
using Microsoft.Extensions.DependencyInjection;
using Color = Android.Graphics.Color;
using IntentFilterAttribute = Android.App.IntentFilterAttribute;
using MetaDataAttribute = Android.App.MetaDataAttribute;
using RemoteViews = Android.Widget.RemoteViews;
using ViewStates = Android.Views.ViewStates;

namespace Helm.Modules.Wallet;

/// <summary>The "Wallet" home-screen widget: this month's spending, the budget, and where the money went.</summary>
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
}

/// <summary>
/// Draws the Wallet widgets. A fixed layout (no list, so no RemoteViewsService); a tap anywhere opens Helm on Wallet.
/// Runs in the app process, possibly without the activity, so services come from <see cref="HelmAndroidServices"/>.
/// The data comes from <see cref="WalletWidgetModel"/> (tested in the core).
/// </summary>
internal static class WalletWidgets
{
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

    public static void Update(Context context, AppWidgetManager manager, int widgetId)
    {
        try
        {
            var (model, enabled) = Load();
            var views = new RemoteViews(context.PackageName, R.Layout(context, "wallet_widget"));
            var off = R.Id(context, "wallet_widget_off");
            var body = R.Id(context, "wallet_widget_body");
            views.SetViewVisibility(off, enabled && model is not null ? ViewStates.Gone : ViewStates.Visible);
            views.SetViewVisibility(body, enabled && model is not null ? ViewStates.Visible : ViewStates.Gone);
            views.SetTextViewText(off, !enabled ? "Wallet is turned off. Open Helm to turn it on." : "Open Helm to load your wallet.");

            if (enabled && model is not null)
            {
                views.SetTextViewText(R.Id(context, "wallet_widget_title"), model.Title);
                views.SetTextViewText(R.Id(context, "wallet_widget_spent"), model.Spent);
                var detail = R.Id(context, "wallet_widget_detail");
                views.SetTextViewText(detail, model.Detail);
                views.SetTextColor(detail, Colour(context, model.IsOver ? "wallet_widget_over" : "wallet_widget_secondary"));
                var bar = R.Id(context, "wallet_widget_bar");
                views.SetViewVisibility(bar, model.HasBudget ? ViewStates.Visible : ViewStates.Gone);
                views.SetProgressBar(bar, 100, model.BudgetPercent, false);
                var overBar = R.Id(context, "wallet_widget_bar_over");
                views.SetViewVisibility(overBar, model.HasBudget && model.IsOver ? ViewStates.Visible : ViewStates.Gone);
                if (model.IsOver) views.SetViewVisibility(bar, ViewStates.Gone);
                views.SetTextViewText(R.Id(context, "wallet_widget_today"), model.Today);
                var pending = R.Id(context, "wallet_widget_pending");
                views.SetTextViewText(pending, model.ToCategorizeText);
                views.SetTextColor(pending, Colour(context, model.ToCategorize > 0 ? "wallet_widget_accent" : "wallet_widget_secondary"));

                for (var i = 0; i < WalletWidgetModel.MaxRows; i++)
                {
                    var n = i + 1;
                    var row = i < model.Rows.Count ? model.Rows[i] : null;
                    views.SetViewVisibility(R.Id(context, $"wallet_widget_row{n}"), row is null ? ViewStates.Gone : ViewStates.Visible);
                    if (row is null) continue;
                    views.SetTextViewText(R.Id(context, $"wallet_widget_name{n}"), row.Name);
                    views.SetTextViewText(R.Id(context, $"wallet_widget_amount{n}"), row.Amount);
                    views.SetProgressBar(R.Id(context, $"wallet_widget_share{n}"), 100, row.Percent, false);
                }
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

    private static (WalletWidgetModel? Model, bool Enabled) Load()
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var store = services.GetRequiredService<WalletStore>();
            var enabled = services.GetRequiredService<WalletCapture>().IsToolEnabled;
            return (WalletWidgetModel.Build(store, store.Now, TimeZoneInfo.Local), enabled);
        }
        catch (Exception ex)
        {
            WalletNotifications.Log(ex, "Could not load the Wallet widget data");
            return (null, true);
        }
    }

    private static Color Colour(Context context, string name) => new(context.GetColor(R.Get(context, name, "color")));
}
