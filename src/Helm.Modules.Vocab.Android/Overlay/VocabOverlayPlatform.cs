using Android.App;
using Android.App.Usage;
using Android.Content;
using Android.Content.Res;
using Android.OS;
using AndroidSettings = Android.Provider.Settings;
using AndroidUri = Android.Net.Uri;

[assembly: UsesPermission("android.permission.SYSTEM_ALERT_WINDOW")]
[assembly: UsesPermission("android.permission.PACKAGE_USAGE_STATS")]

namespace Helm.Modules.Vocab;

/// <summary>
/// What the pop-up card and the bubble need from the phone: whether Helm may draw over other apps, whether it may see
/// which app is in front (usage access), which app that is, and whether someone is using the phone the right way up.
/// </summary>
internal static class VocabOverlayPlatform
{
    /// <summary>How far back to look for the app that came to the front last.</summary>
    private static readonly TimeSpan LookBack = TimeSpan.FromHours(6);

    public static bool CanDraw(Context context) => AndroidSettings.CanDrawOverlays(context);

    /// <summary>Usage access ("Usage data access"), which tells which app is in front.</summary>
    public static bool HasUsageAccess(Context context)
    {
        if (context.GetSystemService(Context.AppOpsService) is not AppOpsManager ops || context.ApplicationInfo is not { } info) return false;
        // Both calls are marked obsolete on some Android version, yet each still answers there; there is no other way to
        // ask (Android 16 only renamed it).
#pragma warning disable CA1422
        var mode = OperatingSystem.IsAndroidVersionAtLeast(29)
            ? ops.UnsafeCheckOpNoThrow(AppOpsManager.OpstrGetUsageStats!, info.Uid, context.PackageName!)
            : ops.CheckOpNoThrow(AppOpsManager.OpstrGetUsageStats!, info.Uid, context.PackageName!);
#pragma warning restore CA1422
        return mode == AppOpsManagerMode.Allowed;
    }

    /// <summary>
    /// The package of the app in front: the last one Android saw come to the front. Null without usage access or when
    /// nothing came to the front for hours.
    /// </summary>
    public static string? ForegroundApp(Context context)
    {
        try
        {
            if (context.GetSystemService(Context.UsageStatsService) is not UsageStatsManager usage) return null;
            var now = Java.Lang.JavaSystem.CurrentTimeMillis();
            var events = usage.QueryEvents(now - (long)LookBack.TotalMilliseconds, now);
            if (events is null) return null;
            string? front = null;
            var e = new UsageEvents.Event();
            while (events.HasNextEvent)
            {
                events.GetNextEvent(e);
                // ACTIVITY_RESUMED (MOVE_TO_FOREGROUND before Android 10): an app's screen came to the front.
                if ((int)e.EventType == 1) front = e.PackageName;
            }
            return front;
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException or Java.Lang.IllegalStateException)
        {
            return null;
        }
    }

    /// <summary>The screen is on and unlocked: someone is looking at the phone.</summary>
    public static bool InUse(Context context) =>
        context.GetSystemService(Context.PowerService) is PowerManager { IsInteractive: true }
        && context.GetSystemService(Context.KeyguardService) is KeyguardManager { IsKeyguardLocked: false };

    /// <summary>Held sideways (games and videos usually are).</summary>
    public static bool Landscape(Context context) => context.Resources?.Configuration?.Orientation == Orientation.Landscape;

    public static bool Dark(Context context) =>
        ((context.Resources?.Configuration?.UiMode ?? UiMode.NightNo) & UiMode.NightMask) == UiMode.NightYes;

    /// <summary>Opens the phone's "Display over other apps" page for Helm.</summary>
    public static void OpenDrawSettings(Context context) =>
        Open(context, new Intent(AndroidSettings.ActionManageOverlayPermission, AndroidUri.Parse("package:" + context.PackageName)));

    /// <summary>Opens the phone's "Usage access" list (Android has no page for one app).</summary>
    public static void OpenUsageSettings(Context context) => Open(context, new Intent(AndroidSettings.ActionUsageAccessSettings));

    private static void Open(Context context, Intent intent)
    {
        intent.AddFlags(ActivityFlags.NewTask);
        try
        {
            context.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            // A phone without that page: the general app settings at least.
            var details = new Intent(AndroidSettings.ActionApplicationDetailsSettings, AndroidUri.Parse("package:" + context.PackageName));
            details.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(details);
        }
    }

    public static int Dp(Context context, double dp) => (int)Math.Round(dp * (context.Resources?.DisplayMetrics?.Density ?? 2));
}
