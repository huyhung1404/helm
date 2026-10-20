using Android.App;
using Android.Content.PM;
using Android.Views;
using Avalonia.Android;
using Helm.App.Android.Services;
using Helm.App.Android.ViewModels;
using Helm.App.Android.Views;
using Helm.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android;

/// <summary>
/// A tool in a window of its own, moved out of Helm's tabs ("Open in new window"). It is an Android document: a card of
/// its own in Recents, beside Helm in split screen, and one window per page (<see cref="ToolWindows"/>). Back leaves
/// the tool's own back stack first, then closes the window; "Move to main window" puts the tool back in Helm's tabs.
/// </summary>
[Activity(
    Label = "Helm",
    Theme = "@style/HelmTheme.NoActionBar",
    Icon = "@drawable/icon",
    Exported = false,
    DocumentLaunchMode = DocumentLaunchMode.IntoExisting,
    AutoRemoveFromRecents = true,
    ResizeableActivity = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class ToolWindowActivity : AvaloniaActivity
{
    public const string ExtraKey = "helm.tool.key";

    private string? _key;

    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        using var finger = ActivityInput.AsFinger(e);
        return base.DispatchTouchEvent(finger ?? e);
    }

    public override bool DispatchKeyEvent(KeyEvent? e) => ActivityInput.ToWebView(this, e) || base.DispatchKeyEvent(e);

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        _key = Intent?.GetStringExtra(ExtraKey);
        var tab = _key is null ? null : App.Services.GetRequiredService<ShellNavigator>().Describe(_key);
        // The view must be there before Avalonia's view is created (in base.OnCreate).
        if (tab is not null) Content = new ToolWindowView(tab, MoveToMain, FinishAndRemoveTask);
        base.OnCreate(savedInstanceState);
        ActivityHost.OnCreated(this);
        if (tab is null)
        {
            // The tool was removed (or turned off) since the window was opened.
            FinishAndRemoveTask();
            return;
        }
        App.Services.GetRequiredService<ToolWindows>().Opened(tab.Key);
        SetTaskDescription(new ActivityManager.TaskDescription($"{tab.Title} - Helm"));
    }

    protected override void OnResume()
    {
        base.OnResume();
        ActivityHost.OnResumed(this);
    }

    protected override void OnPause()
    {
        ActivityHost.OnPaused(this);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (_key is not null) App.Services.GetRequiredService<ToolWindows>().Closed(_key);
        base.OnDestroy();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, global::Android.Content.Intent? data)
    {
        if (!ActivityHost.OnActivityResult(requestCode, resultCode, data)) base.OnActivityResult(requestCode, resultCode, data);
    }

    /// <summary>Back to a tab in Helm's main window, which comes to the front; this window closes.</summary>
    private void MoveToMain()
    {
        if (_key is null) return;
        var services = App.Services;
        services.GetRequiredService<ToolWindows>().Closed(_key);
        var navigator = services.GetRequiredService<ShellNavigator>();
        if (navigator.Describe(_key) is { } tab) navigator.Open(tab);
        _key = null;
        ToolWindows.ShowMain();
        FinishAndRemoveTask();
    }
}
