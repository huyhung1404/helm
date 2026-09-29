using Android.App;
using Android.Content;
using Android.Service.QuickSettings;
using Helm.Core;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// The "Quick Capture" tile in the Quick Settings panel: one tap opens the capture dialog over the current app. On a
/// locked phone it asks to unlock first (notes and tasks are private).
/// </summary>
[Service(Name = QuickCaptureModule.TileName, Label = "Quick Capture", Icon = "@drawable/quick_capture_tile", Exported = true,
    Permission = "android.permission.BIND_QUICK_SETTINGS_TILE")]
[IntentFilter(["android.service.quicksettings.action.QS_TILE"])]
public sealed class QuickCaptureTileService : TileService
{
    public override void OnStartListening()
    {
        base.OnStartListening();
        if (QsTile is not { } tile) return;
        tile.State = IsOn() ? TileState.Inactive : TileState.Unavailable;
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
        var intent = new Intent(this, typeof(QuickCaptureActivity)).AddFlags(ActivityFlags.NewTask);
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            var pending = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            if (pending is not null) StartActivityAndCollapse(pending);
        }
        else
        {
#pragma warning disable CA1422, CS0618 // The Intent overload is the only one before Android 14.
            StartActivityAndCollapse(intent);
#pragma warning restore CA1422, CS0618
        }
    }

    private static bool IsOn()
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
