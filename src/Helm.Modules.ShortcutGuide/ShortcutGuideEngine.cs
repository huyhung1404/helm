using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.ShortcutGuide;

/// <summary>
/// Watches the keyboard (and mouse buttons) through the shared low-level hooks, and shows the guide over the app in
/// front once modifiers were held alone long enough (<see cref="HoldTracker"/>). The hooks only update the tracker
/// and arm a timer; finding the app, building the list and drawing the overlay happen off the hook thread.
/// </summary>
public sealed class ShortcutGuideEngine : IDisposable
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PreviewTime = TimeSpan.FromSeconds(8);

    private readonly LowLevelKeyboardHook _keyboard;
    private readonly LowLevelMouseHook _mouse;
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<ShortcutGuideEngine> _logger;
    private readonly object _gate = new();
    private readonly HoldTracker _tracker;
    private readonly Timer _holdTimer;
    private readonly Timer _watchTimer;
    private readonly Timer _previewTimer;

    private Func<ShortcutGuideSettings>? _settings;
    private Func<ShortcutLibrary>? _library;
    private IDisposable? _keyboardLease;
    private IDisposable? _mouseLease;
    private GuideOverlayWindow? _overlay;
    private int _armedGeneration;
    private int _showSequence; // every show and hide gets a new number; a stale post is ignored
    private string _process = string.Empty;
    private HotkeyModifiers _menuKeysSuppressed;
    private bool _previewing;

    public ShortcutGuideEngine(LowLevelKeyboardHook keyboard, LowLevelMouseHook mouse, IWindowService windows,
        IMonitorService monitors, IUiDispatcher ui, ILogger<ShortcutGuideEngine> logger)
    {
        _keyboard = keyboard;
        _mouse = mouse;
        _windows = windows;
        _monitors = monitors;
        _ui = ui;
        _logger = logger;
        _tracker = new HoldTracker(windows.IsKeyDown);
        _holdTimer = new Timer(_ => Guard(OnHoldTimer));
        _watchTimer = new Timer(_ => Guard(OnWatchTimer));
        _previewTimer = new Timer(_ => Guard(EndPreview));
    }

    public bool IsRunning => _keyboardLease is not null;

    /// <summary>Installs the hooks. Throws <see cref="HookInstallException"/> when Windows refuses them.</summary>
    public void Start(Func<ShortcutGuideSettings> settings, Func<ShortcutLibrary> library)
    {
        _settings = settings;
        _library = library;
        if (_keyboardLease is not null) return;
        _keyboard.Intercept += OnKey;
        _mouse.Intercept += OnMouse;
        try
        {
            _keyboardLease = _keyboard.Acquire();
            _mouseLease = _mouse.Acquire();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        _keyboard.Intercept -= OnKey;
        _mouse.Intercept -= OnMouse;
        _keyboardLease?.Dispose();
        _keyboardLease = null;
        _mouseLease?.Dispose();
        _mouseLease = null;
        lock (_gate) _tracker.Reset();
        Hide();
    }

    /// <summary>Shows the guide for <paramref name="appId"/> as if Ctrl were held, for a few seconds or until a key or click.</summary>
    public void Preview(string appId, HotkeyModifiers held)
    {
        if (_settings is null || _library is null) return;
        var library = _library();
        var app = library.Find(appId);
        var process = app?.Processes.FirstOrDefault() ?? string.Empty;
        var view = GuideContent.Build(library, process, held, _settings().IncludeGeneral) with { AppName = app?.Name ?? "Preview" };
        var monitor = _monitors.FromWindow(_windows.GetForegroundWindow()) ?? _monitors.FromPoint(_windows.GetCursorPosition());
        int sequence;
        lock (_gate)
        {
            _previewing = true;
            sequence = ++_showSequence;
        }
        var position = _settings().Position;
        _ui.Post(() => ShowOnUi(sequence, view, monitor, position));
        _previewTimer.Change(PreviewTime, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        Stop();
        _holdTimer.Dispose();
        _watchTimer.Dispose();
        _previewTimer.Dispose();
    }

    // Hook thread: keep it to bookkeeping.
    private void OnKey(object? sender, KeyboardHookEventArgs e)
    {
        if (e.ExtraInfo == InputSimulator.HelmSignature) return; // our own dummy key
        HoldAction action;
        int generation;
        bool endPreview;
        lock (_gate)
        {
            action = _tracker.OnKey(e.VirtualKey, e.IsKeyDown, e.Time);
            generation = _tracker.Generation;
            endPreview = _previewing && e.IsKeyDown;
        }
        if (endPreview) EndPreview();
        Act(action, generation);
    }

    private void OnMouse(object? sender, MouseHookEventArgs e)
    {
        if (e.Message is not (MouseMessage.LeftDown or MouseMessage.RightDown or MouseMessage.MiddleDown or MouseMessage.Wheel)) return;
        HoldAction action;
        bool endPreview;
        lock (_gate)
        {
            action = _tracker.OnMouseButton();
            endPreview = _previewing && e.Message != MouseMessage.Wheel;
        }
        if (endPreview) EndPreview();
        if (action == HoldAction.Hide) Hide();
    }

    private void Act(HoldAction action, int generation)
    {
        switch (action)
        {
            case HoldAction.Arm:
                Volatile.Write(ref _armedGeneration, generation);
                var delay = Math.Clamp(_settings?.Invoke().HoldDelayMs ?? ShortcutGuideSettings.DefaultHoldDelayMs,
                    ShortcutGuideSettings.MinHoldDelayMs, ShortcutGuideSettings.MaxHoldDelayMs);
                _holdTimer.Change(delay, Timeout.Infinite);
                break;
            case HoldAction.Update:
                ThreadPool.QueueUserWorkItem(_ => Guard(Refresh));
                break;
            case HoldAction.Hide:
                Hide();
                break;
        }
    }

    private void OnHoldTimer()
    {
        if (_settings is null || _library is null) return;
        var generation = Volatile.Read(ref _armedGeneration);
        HotkeyModifiers held;
        lock (_gate)
        {
            if (!_tracker.ShouldShow(generation)) return;
            held = _tracker.Held;
        }

        var settings = _settings();
        if ((held & settings.HoldModifiers) == 0) return;
        var foreground = _windows.GetForegroundWindow();
        var process = _windows.GetProcessName(foreground);
        if (new ProcessExclusions(settings.ExcludedApps).IsExcluded(process)) return;
        if (settings.HideInFullScreen && _windows.IsFullScreenGameRunning()) return;

        var view = GuideContent.Build(_library(), process, held, settings.IncludeGeneral);
        if (view.IsEmpty) return;
        var monitor = _monitors.FromWindow(foreground) ?? _monitors.FromPoint(_windows.GetCursorPosition());

        int sequence;
        lock (_gate)
        {
            if (_tracker.Generation != generation || _tracker.IsSpoiled) return;
            _tracker.MarkShown();
            _process = process;
            _menuKeysSuppressed = HotkeyModifiers.None;
            _previewing = false;
            sequence = ++_showSequence;
        }
        SuppressMenuKeys(held);
        _watchTimer.Change(WatchInterval, WatchInterval);
        _ui.Post(() => ShowOnUi(sequence, view, monitor, settings.Position));
    }

    /// <summary>The held modifiers changed while the guide shows (Ctrl, then Shift too).</summary>
    private void Refresh()
    {
        if (_settings is null || _library is null) return;
        HotkeyModifiers held;
        string process;
        int sequence;
        lock (_gate)
        {
            if (!_tracker.IsShowing) return;
            held = _tracker.Held;
            process = _process;
            sequence = _showSequence;
        }
        var view = GuideContent.Build(_library(), process, held, _settings().IncludeGeneral);
        SuppressMenuKeys(held);
        _ui.Post(() =>
        {
            if (sequence == Volatile.Read(ref _showSequence)) _overlay?.Update(view);
        });
    }

    /// <summary>Safety net: a release the hook missed must not leave the guide on screen.</summary>
    private void OnWatchTimer()
    {
        bool hide;
        lock (_gate)
        {
            if (!_tracker.IsShowing)
            {
                _watchTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            hide = _tracker.CheckReleased();
        }
        if (hide) Hide();
    }

    /// <summary>
    /// Releasing Win alone opens Start, releasing Alt alone moves the focus to the app's menu bar. Once the guide shows,
    /// a harmless key press makes Windows treat the hold as a combination, so letting go does nothing.
    /// </summary>
    private void SuppressMenuKeys(HotkeyModifiers held)
    {
        var needed = held & (HotkeyModifiers.Win | HotkeyModifiers.Alt);
        lock (_gate)
        {
            if ((needed & ~_menuKeysSuppressed) == 0) return;
            _menuKeysSuppressed |= needed;
        }
        InputSimulator.SendDummyKey();
    }

    private void EndPreview()
    {
        lock (_gate)
        {
            if (!_previewing) return;
            _previewing = false;
        }
        Hide();
    }

    private void Hide()
    {
        int sequence;
        lock (_gate)
        {
            sequence = ++_showSequence;
            _previewing = false;
        }
        _watchTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _ui.Post(() =>
        {
            if (sequence == Volatile.Read(ref _showSequence)) _overlay?.HideGuide();
        });
    }

    private void ShowOnUi(int sequence, GuideView view, MonitorInfo? monitor, GuidePosition position)
    {
        if (sequence != Volatile.Read(ref _showSequence)) return;
        try
        {
            _overlay ??= new GuideOverlayWindow(_windows);
            _overlay.ShowGuide(view, monitor, position);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not show the shortcut guide");
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shortcut guide failed");
        }
    }
}
