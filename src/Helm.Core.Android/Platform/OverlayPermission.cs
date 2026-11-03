using Android.Content;
using AndroidSettings = Android.Provider.Settings;
using AndroidUri = Android.Net.Uri;

[assembly: Android.App.UsesPermission("android.permission.SYSTEM_ALERT_WINDOW")]

namespace Helm.Core.Platform;

/// <summary>
/// "Display over other apps", which every window Helm draws over the other apps needs (Vocabulary's card and bubble, Ask
/// Helm's edge handle): whether it is allowed, and the phone's page to allow it.
/// </summary>
public static class OverlayPermission
{
    public static bool Granted(Context context) => AndroidSettings.CanDrawOverlays(context);

    /// <summary>Opens the phone's "Display over other apps" page for Helm (its app page on a phone without one).</summary>
    public static void OpenSettings(Context context)
    {
        var intent = new Intent(AndroidSettings.ActionManageOverlayPermission, AndroidUri.Parse("package:" + context.PackageName));
        intent.AddFlags(ActivityFlags.NewTask);
        try
        {
            context.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            var details = new Intent(AndroidSettings.ActionApplicationDetailsSettings, AndroidUri.Parse("package:" + context.PackageName));
            details.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(details);
        }
    }
}
