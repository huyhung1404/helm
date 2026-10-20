using Android.App;
using Android.Views;

namespace Helm.App.Android;

/// <summary>Input fixes every Helm activity applies (the main window and tool windows).</summary>
internal static class ActivityInput
{
    /// <summary>
    /// Avalonia ignores touches whose tool type is UNKNOWN (it only turns FINGER into touch input). Injected events
    /// report UNKNOWN on older Android (adb "input tap" on Android 9, some automation and accessibility tools), so
    /// such touchscreen events are passed on as a finger: the copy to dispatch instead, or null to keep the event.
    /// </summary>
    public static MotionEvent? AsFinger(MotionEvent? e)
    {
        if (e is null || !e.IsFromSource(InputSourceType.Touchscreen) || !HasUnknownTool(e)) return null;

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
        return MotionEvent.Obtain(e.DownTime, e.EventTime, e.Action, count, properties, coords, e.MetaState,
            e.ButtonState, e.XPrecision, e.YPrecision, e.DeviceId, e.EdgeFlags, e.Source, e.Flags);
    }

    /// <summary>
    /// Avalonia's view takes every key for its own input. A WebView a tool hosts inside it (the SSH terminal) gets the
    /// keys while it has the focus, so typing, Enter and Backspace reach it; Back stays with Helm's navigation, and
    /// Avalonia's own text boxes are untouched. True when the WebView took the key.
    /// </summary>
    public static bool ToWebView(Activity activity, KeyEvent? e) =>
        e is not null && e.KeyCode != Keycode.Back && activity.CurrentFocus is global::Android.Webkit.WebView web && web.DispatchKeyEvent(e);

    private static bool HasUnknownTool(MotionEvent e)
    {
        for (var i = 0; i < e.PointerCount; i++)
            if (e.GetToolType(i) == MotionEventToolType.Unknown) return true;
        return false;
    }
}
