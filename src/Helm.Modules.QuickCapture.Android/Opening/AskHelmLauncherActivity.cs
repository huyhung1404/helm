using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// The separate "Ask Helm" icon in the launcher: Ask Helm's card over whatever app is open, listening. RedMagic's Game
/// Space slider (and any app that opens apps) can point at it. A task of its own (no affinity), so it never brings Helm's
/// window along; hidden by Ways to open's switch (<see cref="AskHelmLauncherIcon"/>). Its filter ranks below Helm's own
/// (priority -1), so Helm's launch intent (GetLaunchIntentForPackage, used by notifications and widgets) stays Helm.
/// </summary>
[Activity(Name = QuickCaptureModule.AskHelmLauncherName, Label = "Ask Helm", Icon = "@mipmap/ask_helm_launcher", Exported = true,
    ExcludeFromRecents = true, TaskAffinity = "", LaunchMode = LaunchMode.SingleTask, Theme = "@android:style/Theme.Translucent.NoTitleBar",
    ConfigurationChanges = AskHelmSelection.Changes, WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden)]
[IntentFilter([Intent.ActionMain], Categories = [Intent.CategoryLauncher], Priority = -1)]
public sealed class AskHelmLauncherActivity : AskHelmActivity
{
}
