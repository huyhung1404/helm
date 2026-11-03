using Android.Appwidget;
using Android.Content;
using Helm.Ai.Assistant;
using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using IntentFilterAttribute = Android.App.IntentFilterAttribute;
using MetaDataAttribute = Android.App.MetaDataAttribute;
using PendingIntent = Android.App.PendingIntent;
using PendingIntentFlags = Android.App.PendingIntentFlags;
using RemoteViews = Android.Widget.RemoteViews;

namespace Helm.Modules.QuickCapture;

/// <summary>The "Ask Helm" home-screen widget: a 4×1 bar with Aa (type), the camera and the mic (listen).</summary>
[BroadcastReceiver(Name = QuickCaptureModule.AskHelmWidgetName, Label = "Ask Helm", Exported = false)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/ask_helm_widget_info")]
public sealed class AskHelmWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        foreach (var id in appWidgetIds) AskHelmWidgets.Update(context, appWidgetManager, id);
    }
}

/// <summary>
/// Draws the Ask Helm widgets: a fixed bar whose buttons open the card over the home screen, each its own way
/// (<see cref="AskHelmOpen"/>). While Quick Capture is off the card is hidden, so the buttons open the tool's page instead.
/// </summary>
internal static class AskHelmWidgets
{
    public static void RefreshAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        var ids = manager?.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(AskHelmWidgetProvider))));
        if (manager is null || ids is not { Length: > 0 }) return;
        foreach (var id in ids) Update(context, manager, id);
    }

    public static void Update(Context context, AppWidgetManager manager, int widgetId)
    {
        try
        {
            var views = new RemoteViews(context.PackageName, Resource.Layout.ask_helm_widget);
            var on = ModuleOn();
            views.SetOnClickPendingIntent(Resource.Id.ask_helm_widget_type, Open(context, widgetId, AskHelmOpenMode.Type, on));
            views.SetOnClickPendingIntent(Resource.Id.ask_helm_widget_camera, Open(context, widgetId, AskHelmOpenMode.Camera, on));
            views.SetOnClickPendingIntent(Resource.Id.ask_helm_widget_mic, Open(context, widgetId, AskHelmOpenMode.Listen, on));
            views.SetOnClickPendingIntent(Resource.Id.ask_helm_widget_root, Open(context, widgetId, AskHelmOpenMode.Listen, on));
            manager.UpdateAppWidget(widgetId, views);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("Helm", $"Could not draw the Ask Helm widget: {ex}");
        }
    }

    private static PendingIntent Open(Context context, int widgetId, AskHelmOpenMode mode, bool on)
    {
        var intent = on ? AskHelmActivity.OpenIntent(context, mode) : OpenTool(context);
        // One request code per widget and button, so each keeps its own extra.
        return PendingIntent.GetActivity(context, widgetId * 4 + (int)mode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private static Intent OpenTool(Context context)
    {
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!) ?? new Intent();
        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(ShellIntents.ExtraModule, QuickCaptureModule.ModuleId);
        return launch;
    }

    private static bool ModuleOn()
    {
        try
        {
            return QuickCaptureModule.IsModuleEnabled(HelmAndroidServices.Current.GetRequiredService<ISettingsStoreFactory>(), QuickCaptureModule.ModuleId);
        }
        catch (Exception)
        {
            return true;
        }
    }
}
