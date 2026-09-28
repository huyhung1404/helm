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
        base.OnPause();
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
        OpenRequestedModule();
        if (_layoutListener is null && Window?.DecorView is { } decor)
        {
            _layoutListener = new ContentInsetsListener(this);
            decor.ViewTreeObserver?.AddOnGlobalLayoutListener(_layoutListener);
        }
        MeasureContentInsets();
    }

    protected override void OnDestroy()
    {
        if (_layoutListener is not null) Window?.DecorView.ViewTreeObserver?.RemoveOnGlobalLayoutListener(_layoutListener);
        _layoutListener = null;
        base.OnDestroy();
    }

    /// <summary>
    /// How far the Avalonia view sits inside the window. Depending on start-up timing Android sometimes lays the
    /// content out below the status bar itself and sometimes behind it; MainView pads only by what is left.
    /// </summary>
    private void MeasureContentInsets()
    {
        var decor = Window?.DecorView;
        if (decor is null || decor.Width == 0) return;
        _avaloniaView ??= FindAvaloniaView(FindViewById(global::Android.Resource.Id.Content));
        if (_avaloniaView is not { Width: > 0 } view) return;
        var location = new int[2];
        view.GetLocationInWindow(location);
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        ContentInsets.Update(new Avalonia.Thickness(
            location[0] / density,
            location[1] / density,
            Math.Max(0, decor.Width - location[0] - view.Width) / density,
            Math.Max(0, decor.Height - location[1] - view.Height) / density));
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
