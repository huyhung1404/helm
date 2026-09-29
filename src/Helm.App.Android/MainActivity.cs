using Android.App;
using Android.Content.PM;
using Android.Views;
using View = Android.Views.View;
using Avalonia.Android;
using Helm.App.Android.ViewModels;
using Helm.App.Android.Views;
using Helm.Core;
using Helm.Core.Modules;
using Helm.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android;

[Activity(
    Label = "Helm",
    Theme = "@style/HelmTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class MainActivity : AvaloniaMainActivity
{
    /// <summary>
    /// Avalonia ignores touches whose tool type is UNKNOWN (it only turns FINGER into touch input). Injected events
    /// report UNKNOWN on older Android (adb "input tap" on Android 9, some automation and accessibility tools), so
    /// such touchscreen events are passed on as a finger.
    /// </summary>
    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        if (e is null || !e.IsFromSource(InputSourceType.Touchscreen) || !HasUnknownTool(e)) return base.DispatchTouchEvent(e);

        var count = e.PointerCount;
        var properties = new MotionEvent.PointerProperties[count];
        var coords = new MotionEvent.PointerCoords[count];
        for (var i = 0; i < count; i++)
        {
            properties[i] = new MotionEvent.PointerProperties();
            e.GetPointerProperties(i, properties[i]);
            if (properties[i].ToolType == MotionEventToolType.Unknown) properties[i].ToolType = MotionEventToolType.Finger;
            coords[i] = new MotionEvent.PointerCoords();
            e.GetPointerCoords(i, coords[i]);
        }
        using var finger = MotionEvent.Obtain(e.DownTime, e.EventTime, e.Action, count, properties, coords, e.MetaState,
            e.ButtonState, e.XPrecision, e.YPrecision, e.DeviceId, e.EdgeFlags, e.Source, e.Flags)!;
        return base.DispatchTouchEvent(finger);
    }

    private View? _avaloniaView;
    private ContentInsetsListener? _layoutListener;

    // Tools reach the activity, its results and the app's foreground/background changes through ActivityHost.
    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActivityHost.OnCreated(this);
    }

    protected override void OnPause()
    {
        ActivityHost.OnPaused(this);
        SetSyncPolling(foreground: false);
        base.OnPause();
    }

    /// <summary>
    /// In front: the live channel is open and the poll runs every 30 s (and right away), so other devices' changes
    /// arrive within seconds; in the background the socket is closed (battery) and the poll runs every 5 min.
    /// </summary>
    private static void SetSyncPolling(bool foreground)
    {
        try
        {
            var engine = App.Services.GetRequiredService<Helm.Core.Sync.SyncEngine>();
            engine.SetPollInterval(foreground ? Helm.Core.Sync.SyncEngine.ForegroundPollInterval : Helm.Core.Sync.SyncEngine.BackgroundPollInterval);
            engine.SetLive(foreground);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not change the sync interval");
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, global::Android.Content.Intent? data)
    {
        if (!ActivityHost.OnActivityResult(requestCode, resultCode, data)) base.OnActivityResult(requestCode, resultCode, data);
    }

    /// <summary>The activity is already running (e.g. opened again from a widget): take the new intent's extras.</summary>
    protected override void OnNewIntent(global::Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null) Intent = intent;
    }

    protected override void OnResume()
    {
        base.OnResume();
        ActivityHost.OnResumed(this);
        SetSyncPolling(foreground: true);
        OpenRequestedModule();
        if (Window?.DecorView is { } decor)
        {
            if (_layoutListener is null)
            {
                _layoutListener = new ContentInsetsListener(this);
                decor.ViewTreeObserver?.AddOnGlobalLayoutListener(_layoutListener);
            }
            // Right after start Android can report the system bars later than the first layout, without another
            // layout pass; measuring again a little later corrects that (it used to wait for the next resume).
            decor.Post(MeasureContentInsets);
            decor.PostDelayed(MeasureContentInsets, 300);
            decor.PostDelayed(MeasureContentInsets, 1000);
        }
        MeasureContentInsets();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) MeasureContentInsets();
    }

    protected override void OnDestroy()
    {
        if (_layoutListener is not null) Window?.DecorView.ViewTreeObserver?.RemoveOnGlobalLayoutListener(_layoutListener);
        _layoutListener = null;
        base.OnDestroy();
    }

    /// <summary>
    /// The padding the shell still needs to stay clear of the status/navigation bars and the display cutout: what they
    /// cover of the window, minus how far Android already placed the Avalonia view inside it. Both come from Android at
    /// the same moment (the window's own insets and the view's position), so start-up timing cannot mix an old value of
    /// one with a new value of the other.
    /// </summary>
    private void MeasureContentInsets()
    {
        var decor = Window?.DecorView;
        if (decor is null || decor.Width == 0) return;
        _avaloniaView ??= FindAvaloniaView(FindViewById(global::Android.Resource.Id.Content));
        if (_avaloniaView is not { Width: > 0 } view) return;
        if (SystemBarInsets(decor) is not { } bars) return; // not attached yet: keep the last padding
        var location = new int[2];
        view.GetLocationInWindow(location);
        var right = decor.Width - location[0] - view.Width;
        var bottom = decor.Height - location[1] - view.Height;
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        var padding = new Avalonia.Thickness(
            Math.Max(0, bars.Left - location[0]) / density,
            Math.Max(0, bars.Top - location[1]) / density,
            Math.Max(0, bars.Right - right) / density,
            Math.Max(0, bars.Bottom - bottom) / density);
        if (ContentInsets.Update(padding))
            Serilog.Log.Information("Shell padding {Padding} dp (system bars {Bars} px, view at {X},{Y} px, window {W}x{H} px)",
                padding, bars, location[0], location[1], decor.Width, decor.Height);
    }

    /// <summary>Status bar, navigation bar and display cutout, in pixels of the window; null until the window has insets.</summary>
    private static (int Left, int Top, int Right, int Bottom)? SystemBarInsets(View decor)
    {
        if (decor.RootWindowInsets is not { } insets) return null;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var i = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            return (i.Left, i.Top, i.Right, i.Bottom);
        }
        (int Left, int Top, int Right, int Bottom) system = (insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
        if (OperatingSystem.IsAndroidVersionAtLeast(28) && insets.DisplayCutout is { } cutout)
            return (Math.Max(system.Left, cutout.SafeInsetLeft), Math.Max(system.Top, cutout.SafeInsetTop),
                Math.Max(system.Right, cutout.SafeInsetRight), Math.Max(system.Bottom, cutout.SafeInsetBottom));
        return system;
    }

    /// <summary>A widget or shortcut can ask for a tool's page with <see cref="ShellIntents.ExtraModule"/>.</summary>
    private void OpenRequestedModule()
    {
        if (Intent?.GetStringExtra(ShellIntents.ExtraModule) is not { Length: > 0 } id) return;
        Intent!.RemoveExtra(ShellIntents.ExtraModule);
        try
        {
            var services = App.Services;
            if (services.GetRequiredService<IModuleHost<IAndroidModule>>().Find(id) is { } module)
                services.GetRequiredService<ShellNavigator>().GoModuleContent(module);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open tool {Id} from an intent", id);
        }
    }

    private static View? FindAvaloniaView(View? view)
    {
        while (view is not null and not Avalonia.Android.AvaloniaView)
            view = view is ViewGroup { ChildCount: > 0 } group ? group.GetChildAt(0) : null;
        return view;
    }

    private sealed class ContentInsetsListener(MainActivity activity) : Java.Lang.Object, ViewTreeObserver.IOnGlobalLayoutListener
    {
        public void OnGlobalLayout() => activity.MeasureContentInsets();
    }

    private static bool HasUnknownTool(MotionEvent e)
    {
        for (var i = 0; i < e.PointerCount; i++)
            if (e.GetToolType(i) == MotionEventToolType.Unknown) return true;
        return false;
    }
}
