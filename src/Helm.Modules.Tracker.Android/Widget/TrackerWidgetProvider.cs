using Android.Appwidget;
using Android.Content;
using IntentFilterAttribute = Android.App.IntentFilterAttribute;
using MetaDataAttribute = Android.App.MetaDataAttribute;

namespace Helm.Modules.Tracker;

/// <summary>The "Tracker" home-screen widget: one workspace's open items, tick them off from the home screen.</summary>
[BroadcastReceiver(Label = "Tracker", Exported = false)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/tracker_widget_info")]
public sealed class TrackerWidgetProvider : AppWidgetProvider
{
    private static int? WidgetId(Intent intent)
    {
        var id = intent.GetIntExtra(AppWidgetManager.ExtraAppwidgetId, AppWidgetManager.InvalidAppwidgetId);
        return id == AppWidgetManager.InvalidAppwidgetId ? null : id;
    }

    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        foreach (var id in appWidgetIds) TrackerWidgets.Update(context, appWidgetManager, id);
        TrackerWidgets.NotifyListChanged(context, appWidgetManager, appWidgetIds);
    }

    public override void OnDeleted(Context? context, int[]? appWidgetIds)
    {
        if (context is null || appWidgetIds is null) return;
        foreach (var id in appWidgetIds) TrackerWidgets.Forget(context, id);
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        base.OnReceive(context, intent);
        if (context is null || intent is null) return;
        switch (intent.Action)
        {
            case TrackerWidgets.ActionItem:
                TrackerWidgets.HandleItem(context, intent);
                break;
            case TrackerWidgets.ActionNextWorkspace:
                if (WidgetId(intent) is { } next) TrackerWidgets.NextWorkspace(context, next);
                break;
            case TrackerWidgets.ActionSync:
                TrackerWidgets.Sync(context, GoAsync());
                break;
            case TrackerWidgets.ActionToggleCompact:
                if (WidgetId(intent) is { } compact) TrackerWidgets.ToggleCompact(context, compact);
                break;
            case TrackerWidgets.ActionToggleMenu:
                if (WidgetId(intent) is { } menu) TrackerWidgets.ToggleMenu(context, menu);
                break;
        }
    }
}
