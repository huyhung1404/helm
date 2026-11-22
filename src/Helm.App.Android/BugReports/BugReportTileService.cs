using Android.App;
using Android.Content;
using Android.Service.QuickSettings;

namespace Helm.App.Android.BugReports;

/// <summary>
/// "Report a bug" in the Quick Settings panel: opens Helm's report page from anywhere, also while another app (or a
/// Helm overlay such as Novel Lens) is in front. On a locked phone it asks to unlock first.
/// </summary>
[Service(Name = "com.huyhung1404.helm.BugReportTileService", Label = "Report a Helm bug", Icon = "@drawable/bug_report_tile", Exported = true,
    Permission = "android.permission.BIND_QUICK_SETTINGS_TILE")]
[IntentFilter(["android.service.quicksettings.action.QS_TILE"])]
public sealed class BugReportTileService : TileService
{
    public override void OnStartListening()
    {
        base.OnStartListening();
        if (QsTile is not { } tile) return;
        tile.State = TileState.Inactive;
        tile.UpdateTile();
    }

    public override void OnClick()
    {
        base.OnClick();
        if (IsLocked) UnlockAndRun(new Java.Lang.Runnable(Open));
        else Open();
    }

    private void Open()
    {
        if (PackageManager?.GetLaunchIntentForPackage(PackageName!) is not { } intent) return;
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        intent.PutExtra(AndroidBugReporter.ExtraBug, AndroidBugReporter.ReportRequest);
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            var pending = PendingIntent.GetActivity(this, 0x4C0001, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            if (pending is not null) StartActivityAndCollapse(pending);
        }
        else
        {
#pragma warning disable CA1422, CS0618 // The Intent overload is the only one before Android 14.
            StartActivityAndCollapse(intent);
#pragma warning restore CA1422, CS0618
        }
    }
}
