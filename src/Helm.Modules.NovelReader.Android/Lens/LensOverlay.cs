using System.Diagnostics;
using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Views;
using Avalonia.Android;
using Microsoft.Extensions.Logging;
using Bitmap = Android.Graphics.Bitmap;

namespace Helm.Modules.NovelReader.Lens;

/// <summary>
/// The lens's two windows over the reading app: a small floating button, and the panel that covers the screen with the
/// page read (<see cref="LensPanel"/>). Both are accessibility overlays (no "draw over other apps" permission). Before
/// a screenshot or a tap both are hidden and let touches through, so the screenshot shows only the reading app and the
/// tap reaches it. The page's text lives in the panel only while it is shown.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
internal sealed class LensOverlay : IDisposable
{
    /// <summary>The floating button's size: a plain Android view (an Avalonia view would fill a far larger window, opaque).</summary>
    private const int BubbleDp = 52;

    private const float TapY = 0.5f;

    /// <summary>Longest wait for the reading app to show the turned page.</summary>
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(2.5);

    private readonly LensService _service;
    private readonly LensTranslator? _translator;
    private readonly ILogger _logger;
    private readonly IWindowManager _windows;
    private readonly global::Android.Views.View _bubble;
    private readonly WindowManagerLayoutParams _bubbleParams;
    private readonly PanelHost _panel;
    private readonly WindowManagerLayoutParams _panelParams;
    private readonly LensPanel _content;
    private readonly LensOcr _ocr = new();
    private bool _bubbleShown, _panelShown, _busy;
    private byte[]? _lastPage;

    public LensOverlay(LensService service, LensTranslator? translator, ILogger logger)
    {
        _service = service;
        _translator = translator;
        _logger = logger;
        _windows = service.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;

        var metrics = service.Resources!.DisplayMetrics!;
        var size = (int)(BubbleDp * metrics.Density);
        _bubble = BubbleView(service, size);
        _bubbleParams = Params(size, size, focusable: false);
        _bubbleParams.Gravity = GravityFlags.Top | GravityFlags.Left;
        _bubbleParams.X = metrics.WidthPixels - size - (int)(8 * metrics.Density);
        _bubbleParams.Y = metrics.HeightPixels / 2 - size / 2;

        _content = new LensPanel(() => TurnAsync(back: true), ReadAsync, () => TurnAsync(back: false), Close,
            size => _translator?.Save(s => s.LensFontSize = size))
        {
            TextSize = translator?.Settings.LensFontSize ?? 18,
            Suggest = chinese => _translator?.SuggestName(chinese) ?? "",
            NovelTitle = () => _translator?.BookTitle,
            SaveEntry = SaveEntryAsync,
            HasEntry = chinese => _translator?.HasEntry(chinese) == true,
            RemoveEntry = RemoveEntryAsync,
        };
        _panel = new PanelHost(service) { Back = Close };
        _panel.AddView(new AvaloniaView(service) { Content = _content });
        // Focusable: the phone's Back closes the lens instead of reaching the reading app behind it, and the
        // keyboard can type a name; the page shrinks above the keyboard.
        _panelParams = Params(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent, focusable: true);
        _panelParams.SoftInputMode = SoftInput.AdjustResize;
    }

    /// <summary>The page shown, kept to convert it again when a name is saved (dropped when the lens closes).</summary>
    private LensPage? _page;

    private async Task SaveEntryAsync(string chinese, string vietnamese, EntryKind kind, bool forNovel)
    {
        if (_translator is null) return;
        _translator.SaveEntry(chinese, vietnamese, kind, forNovel);
        await ReconvertAsync();
    }

    private async Task RemoveEntryAsync(string chinese)
    {
        if (_translator is null) return;
        _translator.RemoveEntry(chinese);
        await ReconvertAsync();
    }

    /// <summary>The page shown converted again (a name was saved or removed), at the same place.</summary>
    private async Task ReconvertAsync()
    {
        if (_translator is null || _page is not { } page) return;
        try
        {
            var rows = await _translator.TranslateAsync(page);
            _content.Show(rows, page.ContinuesFromPrevious, page.ContinuesOnNext, keepPlace: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Novel Lens could not convert the page again");
        }
    }

    private static WindowManagerLayoutParams Params(int width, int height, bool focusable) => new(width, height,
        WindowManagerTypes.AccessibilityOverlay,
        (focusable ? 0 : WindowManagerFlags.NotFocusable) | WindowManagerFlags.LayoutInScreen,
        Format.Translucent);

    /// <summary>The app in front changed: the button shows over a reading app only (the panel closes when it leaves).</summary>
    public void ReadingAppInFront(bool reading)
    {
        if (_busy) return; // our own hide/show and the page turn come back as window changes
        if (reading) ShowBubble();
        else
        {
            HidePanel();
            HideBubble();
        }
    }

    /// <summary>A round "译" button; dragged it moves (kept where it is left), tapped it reads the page.</summary>
    private global::Android.Views.View BubbleView(Context context, int size)
    {
        var background = new global::Android.Graphics.Drawables.GradientDrawable();
        background.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
        background.SetColor(global::Android.Graphics.Color.Argb(0xDD, 0x2D, 0x6C, 0xDF));
        var view = new global::Android.Widget.TextView(context)
        {
            Text = "译",
            Gravity = GravityFlags.Center,
            Background = background,
            ContentDescription = "Novel Lens: read this page",
        };
        view.SetTextColor(global::Android.Graphics.Color.White);
        view.SetTextSize(global::Android.Util.ComplexUnitType.Px, size * 0.45f);
        var slop = ViewConfiguration.Get(context)!.ScaledTouchSlop;
        float downX = 0, downY = 0;
        int startX = 0, startY = 0;
        var dragging = false;
        view.Touch += async (_, e) =>
        {
            var ev = e.Event!;
            switch (ev.ActionMasked)
            {
                case MotionEventActions.Down:
                    (downX, downY, startX, startY, dragging) = (ev.RawX, ev.RawY, _bubbleParams.X, _bubbleParams.Y, false);
                    break;
                case MotionEventActions.Move:
                    var dx = ev.RawX - downX;
                    var dy = ev.RawY - downY;
                    dragging |= Math.Abs(dx) > slop || Math.Abs(dy) > slop;
                    if (!dragging || !_bubbleShown) break;
                    _bubbleParams.X = startX + (int)dx;
                    _bubbleParams.Y = startY + (int)dy;
                    _windows.UpdateViewLayout(view, _bubbleParams);
                    break;
                case MotionEventActions.Up:
                    if (!dragging) await OpenAsync();
                    break;
            }
            e.Handled = true;
        };
        return view;
    }

    private async Task OpenAsync()
    {
        HideBubble();
        await ReadAsync();
    }

    private void Close()
    {
        HidePanel();
        ShowBubble();
    }

    /// <summary>Reads the page shown (the panel hidden while the screenshot is taken) and shows it.</summary>
    private Task ReadAsync() => RunAsync(async watch =>
    {
        var shot = await ScreenshotAsync();
        return (shot, $"Read in {{0}} ms");
    });

    /// <summary>Taps the reading app to turn the page, waits until the new page is shown and still, then reads it.</summary>
    private Task TurnAsync(bool back) => RunAsync(async watch =>
    {
        var metrics = _service.Resources!.DisplayMetrics!;
        var share = (float)Math.Clamp(_translator?.Settings.LensNextTap ?? 0.9, 0.55, 0.98);
        if (!await _service.TapAsync(metrics.WidthPixels * (back ? 1 - share : share), metrics.HeightPixels * TapY))
            throw new InvalidOperationException("Android did not let Helm tap the reading app.");
        var (page, changed) = await WaitForTurnAsync();
        return (page, changed ? "Page turned and read in {0} ms" : "The page did not change (start or end of the chapter?) · {0} ms");
    });

    /// <summary>
    /// One read: both windows hidden, <paramref name="capture"/> (a screenshot, after a tap for Next), then the panel
    /// back with the page recognised and converted. One at a time.
    /// </summary>
    private async Task RunAsync(Func<Stopwatch, Task<(Bitmap Shot, string Status)>> capture)
    {
        if (_busy) return;
        _busy = true;
        var watch = Stopwatch.StartNew();
        try
        {
            await HideForCaptureAsync();
            var (shot, status) = await capture(watch);
            ShowPanel();
            _content.SetBusy(true, "Reading the page…");
            using (shot)
            {
                _lastPage = Signature(shot);
                var lines = await _ocr.ReadAsync(shot);
                var page = LensPageReader.Read(lines, shot.Width);
                _page = page;
                if (page.Paragraphs.Count == 0)
                {
                    _content.Clear();
                    _content.SetStatus("No text found on this page.");
                    return;
                }
                if (_translator is null)
                {
                    _content.SetStatus("Helm's services are not ready: open Helm once, then try again.");
                    return;
                }
                var rows = await _translator.TranslateAsync(page);
                _content.Show(rows, page.ContinuesFromPrevious, page.ContinuesOnNext);
                var names = _translator.BookTitle is { } title ? $" · names of {title}" : "";
                _content.SetStatus(string.Format(status, watch.ElapsedMilliseconds) + names + (_translator.Problem is { } problem ? " · " + problem : ""));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Novel Lens could not read the page");
            ShowPanel();
            _content.SetStatus("Could not read the page: " + ex.Message);
        }
        finally
        {
            _content.SetBusy(false);
            ShowPanel();
            _busy = false;
        }
    }

    /// <summary>
    /// Screenshots until the page differs from the last one read and two shots in a row agree (the page-turn animation
    /// is over); the last shot when nothing changed in time.
    /// </summary>
    private async Task<(Bitmap Page, bool Changed)> WaitForTurnAsync()
    {
        var deadline = Stopwatch.StartNew();
        byte[]? candidate = null;
        Bitmap? last = null;
        while (true)
        {
            await Task.Delay(100);
            var shot = await ScreenshotAsync();
            var signature = Signature(shot);
            last?.Dispose();
            last = shot;
            var changed = _lastPage is null || Differs(signature, _lastPage);
            if (changed && candidate is not null && !Differs(signature, candidate)) return (shot, true);
            candidate = changed ? signature : null;
            if (deadline.Elapsed > TurnTimeout) return (shot, changed);
        }
    }

    /// <summary>A screenshot, tried again shortly when Android finds it too soon after the last one.</summary>
    private async Task<Bitmap> ScreenshotAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _service.ScreenshotAsync();
            }
            catch (ScreenshotRefusedException ex) when (ex.TooSoon && attempt < 15)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>A coarse grey thumbnail of the page area (status bar and the app's footer left out), to see a page change.</summary>
    private static byte[] Signature(Bitmap shot)
    {
        const int W = 24, H = 48;
        var top = shot.Height * 6 / 100;
        var height = shot.Height * 86 / 100;
        using var page = Bitmap.CreateBitmap(shot, 0, top, shot.Width, height)!;
        using var small = Bitmap.CreateScaledBitmap(page, W, H, true)!;
        var pixels = new int[W * H];
        small.GetPixels(pixels, 0, W, 0, 0, W, H);
        var grey = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            grey[i] = (byte)((((p >> 16) & 0xFF) * 3 + ((p >> 8) & 0xFF) * 6 + (p & 0xFF)) / 10);
        }
        return grey;
    }

    /// <summary>Whether two page thumbnails show different pages (a few cells changed clearly, not just noise).</summary>
    private static bool Differs(byte[] a, byte[] b)
    {
        var changed = 0;
        for (var i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 24) changed++;
        return changed > a.Length / 100;
    }

    /// <summary>Hides both windows and lets touches through; waits a couple of frames so the screen no longer shows them.</summary>
    private async Task HideForCaptureAsync()
    {
        if (_panelShown)
        {
            _panel.Visibility = ViewStates.Invisible;
            _panelParams.Flags |= WindowManagerFlags.NotTouchable;
            _windows.UpdateViewLayout(_panel, _panelParams);
        }
        if (_bubbleShown) _bubble.Visibility = ViewStates.Invisible;
        await Task.Delay(80);
    }

    private void ShowPanel()
    {
        _panelParams.Flags &= ~WindowManagerFlags.NotTouchable;
        if (!_panelShown)
        {
            _windows.AddView(_panel, _panelParams);
            _panelShown = true;
        }
        else _windows.UpdateViewLayout(_panel, _panelParams);
        _panel.Visibility = ViewStates.Visible;
    }

    private void HidePanel()
    {
        if (!_panelShown) return;
        _windows.RemoveView(_panel);
        _panelShown = false;
        _lastPage = null;
        _page = null;
        _content.Clear();
    }

    private void ShowBubble()
    {
        if (_panelShown) return;
        _bubble.Visibility = ViewStates.Visible;
        if (_bubbleShown) return;
        _windows.AddView(_bubble, _bubbleParams);
        _bubbleShown = true;
    }

    private void HideBubble()
    {
        if (!_bubbleShown) return;
        _windows.RemoveView(_bubble);
        _bubbleShown = false;
    }

    public void Dispose()
    {
        HidePanel();
        HideBubble();
        _ocr.Dispose();
    }

    /// <summary>Holds the panel's Avalonia view; the phone's Back closes the lens.</summary>
    private sealed class PanelHost(Context context) : global::Android.Widget.FrameLayout(context)
    {
        public Action? Back { get; init; }

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (e?.KeyCode != Keycode.Back) return base.DispatchKeyEvent(e);
            if (e.Action == KeyEventActions.Up) Back?.Invoke();
            return true;
        }
    }
}
