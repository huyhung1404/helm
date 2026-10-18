using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views.Accessibility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Helm.Core;

namespace Helm.Modules.NovelReader.Lens;

/// <summary>
/// Novel Lens: while a reading app is in front, a small button floats over it; pressed, Helm reads the page shown (a
/// screenshot, then text recognition) and covers the screen with it in study form. Next taps the reading app to turn
/// the page and reads the new one. One accessibility service does all three (screenshot, overlay, tap), so the user
/// grants a single permission. The page's text stays in memory for the page shown only.
/// </summary>
// A fixed name: Android remembers the enabled accessibility services by it, so it must never change between versions.
[Service(Name = "com.huyhung1404.helm.novel.LensService", Label = "Helm Novel Lens",
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
[IntentFilter(["android.accessibilityservice.AccessibilityService"])]
[MetaData("android.accessibilityservice", Resource = "@xml/helm_novel_lens_service")]
// Screenshots from an accessibility service came with Android 11.
[System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
public sealed class LensService : AccessibilityService
{
    /// <summary>The reading apps the lens shows itself over: Fanqie (番茄小说), its overseas and Chinese builds.</summary>
    public static readonly IReadOnlySet<string> ReadingApps = new HashSet<string>(StringComparer.Ordinal)
    {
        "com.dragon.read.oversea.gp",
        "com.dragon.read",
    };

    private ILogger _logger = NullLogger.Instance;
    private LensOverlay? _overlay;

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        try
        {
            LensTranslator? translator = null;
            if (HelmAndroidServices.IsConfigured)
            {
                var services = HelmAndroidServices.Current;
                _logger = (ILogger?)services.GetService<ILoggerFactory>()?.CreateLogger<LensService>() ?? NullLogger.Instance;
                translator = services.GetService<LensTranslator>();
            }
            _overlay = new LensOverlay(this, translator, _logger);
            _logger.LogInformation("Novel Lens connected");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Novel Lens could not start");
        }
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        if (e is null || e.EventType != EventTypes.WindowStateChanged || _overlay is null) return;
        var package = e.PackageName?.ToString();
        // The system's windows (status bar, keyboard) do not change which app is in front.
        if (package is null || package == "com.android.systemui" || IsKeyboard(e)) return;
        // Helm's own lens windows do not either; Helm's own screen (an activity) does.
        if (package == PackageName && e.ClassName?.ToString()?.Contains("Activity", StringComparison.Ordinal) != true) return;
        _overlay.ReadingAppInFront(ReadingApps.Contains(package));
    }

    private static bool IsKeyboard(AccessibilityEvent e) =>
        e.ClassName?.ToString() is { } name && name.Contains("InputMethod", StringComparison.Ordinal);

    public override void OnInterrupt()
    {
    }

    public override bool OnUnbind(Intent? intent)
    {
        _overlay?.Dispose();
        _overlay = null;
        return base.OnUnbind(intent);
    }

    /// <summary>A screenshot of the whole display as a software bitmap; throws when Android refuses (too soon after the last one).</summary>
    public Task<Bitmap> ScreenshotAsync()
    {
        var done = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        TakeScreenshot(0, MainExecutor!, new ScreenshotCallback(done));
        return done.Task;
    }

    /// <summary>A short tap at a point of the screen (pixels), sent to the app in front.</summary>
    public Task<bool> TapAsync(float x, float y)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var path = new Android.Graphics.Path();
        path.MoveTo(x, y);
        var gesture = new GestureDescription.Builder()
            .AddStroke(new GestureDescription.StrokeDescription(path, 0, 40))!
            .Build()!;
        if (!DispatchGesture(gesture, new GestureCallback(done), null)) done.TrySetResult(false);
        return done.Task;
    }

    private sealed class ScreenshotCallback(TaskCompletionSource<Bitmap> done) : Java.Lang.Object, ITakeScreenshotCallback
    {
        public void OnSuccess(ScreenshotResult screenshot)
        {
            try
            {
                using var buffer = screenshot.HardwareBuffer;
                using var hardware = Bitmap.WrapHardwareBuffer(buffer, screenshot.ColorSpace)
                    ?? throw new InvalidOperationException("The screenshot could not be read");
                // Hardware bitmaps cannot be read pixel by pixel; text recognition and the change check need a copy.
                var copy = hardware.Copy(Bitmap.Config.Argb8888!, false) ?? throw new InvalidOperationException("The screenshot could not be copied");
                done.TrySetResult(copy);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }

        public void OnFailure(int errorCode) => done.TrySetException(new ScreenshotRefusedException(errorCode));
    }

    private sealed class GestureCallback(TaskCompletionSource<bool> done) : GestureResultCallback
    {
        public override void OnCompleted(GestureDescription? gestureDescription) => done.TrySetResult(true);
        public override void OnCancelled(GestureDescription? gestureDescription) => done.TrySetResult(false);
    }
}

/// <summary>Android refused a screenshot; <see cref="TooSoon"/> when it came too soon after the last one (try again shortly).</summary>
public sealed class ScreenshotRefusedException(int errorCode) : Exception($"Android refused the screenshot (error {errorCode})")
{
    public int ErrorCode { get; } = errorCode;

    // AccessibilityService.ERROR_TAKE_SCREENSHOT_INTERVAL_TIME_SHORT
    public bool TooSoon => ErrorCode == 3;
}
