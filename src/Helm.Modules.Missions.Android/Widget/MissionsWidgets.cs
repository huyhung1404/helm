using Android.Appwidget;
using Android.Content;
using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntentFilterAttribute = Android.App.IntentFilterAttribute;
using MetaDataAttribute = Android.App.MetaDataAttribute;
using RemoteViews = Android.Widget.RemoteViews;
using ViewStates = Android.Views.ViewStates;
using Color = Android.Graphics.Color;

namespace Helm.Modules.Missions;

/// <summary>The "Missions" home-screen widget: the missions in progress and the step each one is on.</summary>
[BroadcastReceiver(Label = "Missions", Exported = false)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/missions_widget_info")]
public sealed class MissionsWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        foreach (var id in appWidgetIds) MissionsWidgets.Update(context, appWidgetManager, id);
    }
}

/// <summary>
/// Draws the Missions widgets. A fixed layout of three rows (no list, so no RemoteViewsService); a tap anywhere opens
/// Helm on Missions. Runs in the app process, possibly without the activity, so services come from
/// <see cref="HelmAndroidServices"/>. The data comes from <see cref="MissionWidgetModel"/> (tested in the core).
/// </summary>
internal static class MissionsWidgets
{
    private const int Rows = MissionWidgetModel.MaxRows;

    public static void RefreshAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        var ids = manager.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(MissionsWidgetProvider))));
        if (ids is not { Length: > 0 }) return;
        foreach (var id in ids) Update(context, manager, id);
    }

    /// <summary>Whether the launcher can place a widget when the app asks (Android 8+, most launchers).</summary>
    public static bool CanRequestPin(Context context)
    {
        try { return AppWidgetManager.GetInstance(context)?.IsRequestPinAppWidgetSupported == true; }
        catch (Exception) { return false; }
    }

    /// <summary>Asks the launcher to add the Missions widget; the launcher shows its own confirmation.</summary>
    public static void RequestPin(Context context)
    {
        try
        {
            var provider = new ComponentName(context, Java.Lang.Class.FromType(typeof(MissionsWidgetProvider)));
            AppWidgetManager.GetInstance(context)?.RequestPinAppWidget(provider, null, null);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not ask the launcher to add the Missions widget");
        }
    }

    public static void Update(Context context, AppWidgetManager manager, int widgetId)
    {
        try
        {
            var (model, enabled) = Load();
            var views = new RemoteViews(context.PackageName, R.Layout(context, "missions_widget"));
            var empty = R.Id(context, "missions_widget_empty");
            var emptyText = enabled ? model.EmptyText : "Missions is turned off. Open Helm to turn it on.";
            var showRows = enabled && !model.IsEmpty;
            views.SetViewVisibility(empty, showRows ? ViewStates.Gone : ViewStates.Visible);
            views.SetTextViewText(empty, emptyText);

            var more = R.Id(context, "missions_widget_more");
            views.SetViewVisibility(more, showRows && model.More > 0 ? ViewStates.Visible : ViewStates.Gone);
            views.SetTextViewText(more, $"+{model.More} more");

            for (var i = 0; i < Rows; i++)
            {
                var n = i + 1;
                var row = showRows && i < model.Rows.Count ? model.Rows[i] : null;
                views.SetViewVisibility(R.Id(context, $"missions_widget_row{n}"), row is null ? ViewStates.Gone : ViewStates.Visible);
                if (row is null) continue;
                views.SetTextViewText(R.Id(context, $"missions_widget_title{n}"), row.Title);
                views.SetTextViewText(R.Id(context, $"missions_widget_step{n}"), row.Step);
                views.SetProgressBar(R.Id(context, $"missions_widget_bar{n}"), 100, row.Percent, false);
                var progress = R.Id(context, $"missions_widget_progress{n}");
                views.SetTextViewText(progress, row.Progress);
                views.SetTextColor(progress, Colour(context, row.IsBehind ? "missions_widget_behind" : "missions_widget_secondary"));
            }

            if (MissionReminders.OpenMissionsIntent(context, widgetId * 8 + 1) is { } open)
                views.SetOnClickPendingIntent(R.Id(context, "missions_widget_root"), open);
            manager.UpdateAppWidget(widgetId, views);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not draw the Missions widget");
        }
    }

    private static (MissionWidgetModel Model, bool Enabled) Load()
    {
        try
        {
            var services = HelmAndroidServices.Current;
            var store = services.GetRequiredService<MissionsStore>();
            var general = services.GetRequiredService<ISettingsStoreFactory>().Get<GeneralSettings>(GeneralSettings.StoreId).Current;
            var enabled = !general.EnabledModules.TryGetValue(MissionsIds.ModuleId, out var on) || on;
            return (MissionWidgetModel.Build(store, store.Now, TimeZoneInfo.Local), enabled);
        }
        catch (Exception ex)
        {
            Log(ex, "Could not load the Missions widget data");
            return (new MissionWidgetModel([], "Open Helm to load your missions.", 0), true);
        }
    }

    private static Color Colour(Context context, string name) => new(context.GetColor(R.Get(context, name, "color")));

    private static void Log(Exception ex, string message)
    {
        try
        {
            HelmAndroidServices.Current.GetService<ILoggerFactory>()?.CreateLogger("Missions.Widget").LogWarning(ex, message);
        }
        catch (Exception)
        {
            Android.Util.Log.Warn("Helm", $"{message}: {ex}");
        }
    }
}
