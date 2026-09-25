using System.Media;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Hooks;
using Helm.Core.Interop;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.AlwaysOnTop;

public sealed record PinnedWindowInfo(nint Handle, string Title, string ProcessName);

/// <summary>
/// All pin state lives on one <see cref="MessageLoopThread"/>: WinEvents arrive there, border overlays are created
/// there, and public calls are posted there — so no locking is needed.
/// </summary>
internal sealed class AlwaysOnTopEngine : IAsyncDisposable
{
    private readonly IWindowService _windows;
    private readonly ILogger _logger;
    private readonly MessageLoopThread _thread;
    private readonly Dictionary<nint, Pinned> _pinned = new();
    private WinEventHook? _hook;
    private Options _options;

    private AlwaysOnTopEngine(IWindowService windows, ILogger logger, Options options)
    {
        _windows = windows;
        _logger = logger;
        _options = options;
        _thread = new MessageLoopThread("Helm.AlwaysOnTop", logger);
    }

    /// <summary>Raised on the engine thread with a fresh snapshot whenever the pinned set changes.</summary>
    public event Action<IReadOnlyList<PinnedWindowInfo>>? PinnedChanged;

    public static async Task<AlwaysOnTopEngine> StartAsync(IWindowService windows, ILogger logger, Options options)
    {
        var engine = new AlwaysOnTopEngine(windows, logger, options);
        try
        {
            engine._hook = await WinEventHook.InstallAsync(engine._thread,
            [
                (WinEvents.SystemForeground, WinEvents.SystemForeground),
                (WinEvents.SystemMinimizeStart, WinEvents.SystemMinimizeEnd),
                (WinEvents.ObjectDestroy, WinEvents.ObjectHide),
                (WinEvents.ObjectLocationChange, WinEvents.ObjectLocationChange),
                (WinEvents.ObjectCloaked, WinEvents.ObjectUncloaked),
            ], engine.OnWinEvent, logger).ConfigureAwait(false);
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return engine;
    }

    public void UpdateOptions(Options options) => _thread.Post(() =>
    {
        _options = options;
        foreach (var p in _pinned.Values) SyncBorder(p);
    });

    /// <summary>Pins or unpins the current foreground window (hotkey handler; callable from any thread).</summary>
    public void ToggleForeground() => _thread.Post(() =>
    {
        var hwnd = _windows.GetForegroundWindow();
        if (_pinned.ContainsKey(hwnd))
        {
            Unpin(hwnd, restoreZOrder: true);
            Beep(pinned: false);
            return;
        }
        TryPin(hwnd);
    });

    public void Unpin(nint hwnd) => _thread.Post(() => Unpin(hwnd, restoreZOrder: true));

    public Task UnpinAllAsync() => _thread.InvokeAsync(() =>
    {
        foreach (var hwnd in _pinned.Keys.ToList()) Unpin(hwnd, restoreZOrder: true, notify: false);
        Notify();
    });

    public async ValueTask DisposeAsync()
    {
        try
        {
            await UnpinAllAsync().ConfigureAwait(false);
            await _thread.InvokeAsync(() => _hook?.Dispose()).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        _thread.Dispose();
    }

    private void TryPin(nint hwnd)
    {
        if (!_windows.IsAppWindow(hwnd))
        {
            _logger.LogDebug("Ignoring pin request for non-app window 0x{Hwnd:X}", hwnd);
            return;
        }
        var process = _windows.GetProcessName(hwnd);
        if (_options.Exclusions.IsExcluded(process))
        {
            _logger.LogInformation("Not pinning {Process}: excluded", process);
            return;
        }
        if (_options.DoNotActivateInGameMode && _windows.IsFullScreenGameRunning())
        {
            _logger.LogInformation("Not pinning: a full-screen game or presentation is running");
            return;
        }
        if (!_windows.SetTopmost(hwnd, true))
        {
            _logger.LogWarning("SetWindowPos(HWND_TOPMOST) failed for 0x{Hwnd:X} ({Process})", hwnd, process);
            return;
        }

        var pinned = new Pinned(hwnd, _windows.GetTitle(hwnd), process);
        _pinned[hwnd] = pinned;
        SyncBorder(pinned);
        Beep(pinned: true);
        _logger.LogInformation("Pinned 0x{Hwnd:X} {Process} \"{Title}\"", hwnd, process, pinned.Title);
        Notify();
    }

    private void Unpin(nint hwnd, bool restoreZOrder, bool notify = true)
    {
        if (!_pinned.Remove(hwnd, out var pinned)) return;
        pinned.Border?.Dispose();
        if (restoreZOrder && _windows.IsWindow(hwnd)) _windows.SetTopmost(hwnd, false);
        _logger.LogInformation("Unpinned 0x{Hwnd:X} {Process}", hwnd, pinned.ProcessName);
        if (notify) Notify();
    }

    private void OnWinEvent(WinEventArgs e)
    {
        if (!e.IsWindowEvent || !_pinned.TryGetValue(e.Hwnd, out var pinned)) return;

        switch (e.Event)
        {
            case WinEvents.ObjectDestroy:
                Unpin(e.Hwnd, restoreZOrder: false);
                return;
            case WinEvents.SystemForeground:
                // Activating a pinned window lifts it above its border; put the borders back on top.
                foreach (var p in _pinned.Values) p.Border?.BringToTop();
                RefreshTitle(pinned);
                return;
            default:
                SyncBorder(pinned);
                return;
        }
    }

    private void RefreshTitle(Pinned pinned)
    {
        var title = _windows.GetTitle(pinned.Handle);
        if (title == pinned.Title) return;
        pinned.Title = title;
        Notify();
    }

    /// <summary>Creates/moves/hides the border so it tracks the window's visible frame.</summary>
    private void SyncBorder(Pinned pinned)
    {
        var hwnd = pinned.Handle;
        var visible = _options.ShowBorder
                      && _windows.IsVisible(hwnd)
                      && !_windows.IsMinimized(hwnd)
                      && !_windows.IsCloaked(hwnd);

        if (!visible)
        {
            pinned.Border?.Hide();
            return;
        }

        if (pinned.Border is null)
        {
            try
            {
                pinned.Border = OverlayWindow.Create(canvas =>
                {
                    var o = _options;
                    canvas.Frame(new PixelRect(0, 0, canvas.Width, canvas.Height), o.Color, o.Thickness);
                }, title: "Helm pinned border");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not create border window");
                return;
            }
        }

        if (_windows.GetFrameBounds(hwnd) is not { } frame) return;
        pinned.Border.SetBounds(frame);
        pinned.Border.Invalidate();
        pinned.Border.Show();
    }

    private void Beep(bool pinned)
    {
        if (!_options.PlaySound) return;
        try
        {
            (pinned ? SystemSounds.Asterisk : SystemSounds.Beep).Play();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not play sound");
        }
    }

    private void Notify()
    {
        var snapshot = _pinned.Values.Select(p => new PinnedWindowInfo(p.Handle, p.Title, p.ProcessName)).ToList();
        PinnedChanged?.Invoke(snapshot);
    }

    internal sealed record Options(
        bool ShowBorder,
        RgbColor Color,
        int Thickness,
        bool PlaySound,
        bool DoNotActivateInGameMode,
        ProcessExclusions Exclusions)
    {
        public static Options From(AlwaysOnTopSettings s) => new(
            s.ShowBorder,
            s.UseAccentColor ? AccentColor.Current : RgbColor.Parse(s.BorderColor, AccentColor.Current),
            Math.Clamp(s.BorderThickness, 1, 20),
            s.PlaySound,
            s.DoNotActivateInGameMode,
            new ProcessExclusions(s.ExcludedApps));
    }

    private sealed class Pinned(nint handle, string title, string processName)
    {
        public nint Handle { get; } = handle;
        public string Title { get; set; } = title;
        public string ProcessName { get; } = processName;
        public OverlayWindow? Border { get; set; }
    }
}
