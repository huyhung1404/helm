using Android.App;
using Android.Content.PM;
using Android.Views;
using Avalonia.Android;

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

    private static bool HasUnknownTool(MotionEvent e)
    {
        for (var i = 0; i < e.PointerCount; i++)
            if (e.GetToolType(i) == MotionEventToolType.Unknown) return true;
        return false;
    }
}
