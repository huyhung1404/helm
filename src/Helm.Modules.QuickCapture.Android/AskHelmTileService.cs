using Android.App;
using Android.Content;
using Android.Service.QuickSettings;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// The "Ask Helm" tile in the Quick Settings panel: one tap opens Ask Helm's card over the current app, listening. On a
/// locked phone it asks to unlock first (it can read and change the user's data).
/// </summary>
[Service(Name = QuickCaptureModule.AskHelmTileName, Label = "Ask Helm", Icon = "@drawable/ask_helm_tile", Exported = true,
    Permission = "android.permission.BIND_QUICK_SETTINGS_TILE")]
[IntentFilter(["android.service.quicksettings.action.QS_TILE"])]
public sealed class AskHelmTileService : TileService
{
    public override void OnStartListening()
    {
        base.OnStartListening();
        // Only a tile in the panel listens: Ways to open shows it as added (also for tiles added before it asked).
        AskHelmTileState.Set(this, true);
        if (QsTile is not { } tile) return;
        tile.State = TileState.Inactive;
        tile.UpdateTile();
    }

    public override void OnTileAdded()
    {
        base.OnTileAdded();
        AskHelmTileState.Set(this, true);
    }

    public override void OnTileRemoved()
    {
        base.OnTileRemoved();
        AskHelmTileState.Set(this, false);
    }

    public override void OnClick()
    {
        base.OnClick();
        if (IsLocked) UnlockAndRun(new Java.Lang.Runnable(Open));
        else Open();
    }

    private void Open()
    {
        var intent = new Intent(this, typeof(AskHelmActivity)).AddFlags(ActivityFlags.NewTask);
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            var pending = PendingIntent.GetActivity(this, 1, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
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
