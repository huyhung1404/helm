using Android.Appwidget;
using Android.Content;
using Android.Content.PM;
using Helm.Ai.Assistant;
using Helm.Core.Platform;
using AndroidApp = Android.App.Application;

namespace Helm.Modules.QuickCapture;

/// <summary>What the "Ways to open" page asks of the phone (<see cref="IAskHelmOpenPlatform"/>).</summary>
internal sealed class AskHelmOpenPlatform : IAskHelmOpenPlatform
{
    private static Context Context => AndroidApp.Context;

    public bool CanDrawOverlays() => OverlayPermission.Granted(Context);

    public void OpenOverlaySettings() => OverlayPermission.OpenSettings(Context);

    public bool CanPinWidget()
    {
        try { return AppWidgetManager.GetInstance(Context)?.IsRequestPinAppWidgetSupported == true; }
        catch (Exception) { return false; }
    }

    public void PinWidget()
    {
        try
        {
            var provider = new ComponentName(Context, Java.Lang.Class.FromType(typeof(AskHelmWidgetProvider)));
            AppWidgetManager.GetInstance(Context)?.RequestPinAppWidget(provider, null, null);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("Helm", $"Could not ask the launcher to add the Ask Helm widget: {ex.Message}");
        }
    }

    public bool TileAdded() => AskHelmTileState.Added(Context);
}

/// <summary>
/// Whether the Ask Helm tile is in the Quick Settings panel. Android has no question for it, so the tile says it
/// itself: when it is added or removed, and each time the panel shows it.
/// </summary>
internal static class AskHelmTileState
{
    private const string Prefs = "helm_ask_open";
    private const string TileKey = "tile_added";

    public static bool Added(Context context) => context.GetSharedPreferences(Prefs, FileCreationMode.Private)?.GetBoolean(TileKey, false) == true;

    public static void Set(Context context, bool added)
    {
        if (Added(context) == added) return;
        context.GetSharedPreferences(Prefs, FileCreationMode.Private)?.Edit()?.PutBoolean(TileKey, added)?.Apply();
    }
}

/// <summary>The separate "Ask Helm" icon in the launcher: shown while Quick Capture is on and its switch is on.</summary>
internal static class AskHelmLauncherIcon
{
    public static void Apply(Context context, bool shown)
    {
        var component = new ComponentName(context, QuickCaptureModule.AskHelmLauncherName);
        var state = shown ? ComponentEnabledState.Enabled : ComponentEnabledState.Disabled;
        if (context.PackageManager?.GetComponentEnabledSetting(component) == state) return;
        context.PackageManager?.SetComponentEnabledSetting(component, state, ComponentEnableOption.DontKillApp);
    }
}
