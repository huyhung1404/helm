using Android.Content;
using Helm.App.Android.ViewModels;
using Helm.Core.Platform;

namespace Helm.App.Android.Services;

/// <summary>
/// Tools moved out of Helm's tabs into windows of their own (<see cref="ToolWindowActivity"/>). Each is an Android
/// document task named by its tab key, so Android lists it apart in Recents, puts it beside Helm in split screen, and
/// starting the same key again brings the existing window up instead of opening a second one.
/// </summary>
public sealed class ToolWindows
{
    private readonly HashSet<string> _open = [];

    /// <summary>Whether the page has a live window of its own.</summary>
    public bool IsOpen(string key) => _open.Contains(key);

    /// <summary>Brings the page's window to the front; false when it has none.</summary>
    public bool BringToFront(string key)
    {
        if (!_open.Contains(key)) return false;
        Start(key);
        return true;
    }

    public void Open(ShellTab tab) => Start(tab.Key);

    internal void Opened(string key) => _open.Add(key);

    internal void Closed(string key) => _open.Remove(key);

    private static void Start(string key)
    {
        var context = (Context?)ActivityHost.Latest ?? global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(ToolWindowActivity));
        // The data names the document: the same key finds the same task again.
        intent.SetData(global::Android.Net.Uri.Parse("helm-tool://page/" + Uri.EscapeDataString(key)));
        intent.PutExtra(ToolWindowActivity.ExtraKey, key);
        // Beside Helm when the screen is split; a window of its own (a card in Recents) otherwise.
        intent.AddFlags(ActivityFlags.NewDocument | ActivityFlags.LaunchAdjacent);
        if (context is not global::Android.App.Activity) intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    /// <summary>Brings Helm's main window (its tabs) to the front.</summary>
    public static void ShowMain()
    {
        var context = global::Android.App.Application.Context;
        if (context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!) is not { } intent) return;
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
        context.StartActivity(intent);
    }
}
