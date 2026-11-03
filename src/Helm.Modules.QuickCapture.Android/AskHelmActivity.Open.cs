using Android.Content;
using Helm.Ai.Assistant;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// How the card starts, from the way it was opened (<see cref="AskHelmOpen.ExtraOpen"/>): listening (the tile, the edge
/// handle, the widget's mic, the "Ask Helm" icon), typing (the widget's Aa) or the camera (the widget's camera).
/// </summary>
public partial class AskHelmActivity
{
    /// <summary>An intent that opens the card over the current app in <paramref name="mode"/>.</summary>
    public static Intent OpenIntent(Context context, AskHelmOpenMode mode) =>
        new Intent().SetClassName(context.PackageName!, QuickCaptureModule.AskHelmActivityName)
            .AddFlags(ActivityFlags.NewTask)
            .PutExtra(AskHelmOpen.ExtraOpen, AskHelmOpen.Value(mode));

    private void StartOpened(Intent? intent)
    {
        switch (AskHelmOpen.Parse(intent?.GetStringExtra(AskHelmOpen.ExtraOpen)))
        {
            case AskHelmOpenMode.Type:
                ShowTyping(null);
                break;
            case AskHelmOpenMode.Camera:
                var opened = false;
                OpenCamera(ref opened);
                if (!opened) Listen();
                break;
            default:
                Listen();
                break;
        }
    }

    /// <summary>The card's camera, where the card has one: sets <paramref name="opened"/>. Without it the card listens.</summary>
    partial void OpenCamera(ref bool opened);
}
