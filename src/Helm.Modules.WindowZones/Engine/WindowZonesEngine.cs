using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Interop;
using Helm.Modules.WindowZones.Model;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.WindowZones.Engine;

/// <summary>Shows the zone's windows as tabs for a moment (implemented on the UI thread).</summary>
public interface IZoneTabStrip
{
    void Show(TabStripView view);

    void Hide();
}

/// <summary>
/// Zones as drawers of windows. Everything stateful runs on one <see cref="MessageLoopThread"/> ("Helm.WindowZones"):
/// WinEvents arrive there, the GDI overlay lives there, and hook callbacks only read volatile flags and post small work
/// items to it, so they return within microseconds.
/// <list type="bullet">
/// <item>Hold the drag key while dragging a window: the zones of that monitor show; drop it in one and it fits it.</item>
/// <item>A zone's shortcut opens it (its windows come back), brings it to the front, or closes it (minimizes them).</item>
/// <item>The next/previous shortcuts switch between the windows of the zone in front.</item>
/// </list>
/// </summary>
internal sealed class WindowZonesEngine : IAsyncDisposable
{
    private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(1400);

    /// <summary>How far (px at 100 %) a window in a zone must be dragged before it leaves the zone.</summary>
    private const int LeaveDistance = 12;

    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly WindowZonesData _data;
    private readonly IZoneTabStrip _tabStrip;
    private readonly LowLevelMouseHook? _mouseHook;
    private readonly LowLevelKeyboardHook? _keyboardHook;
    private readonly ILogger _logger;
    private readonly MessageLoopThread _thread;
    private readonly HashSet<int> _swallowedKeyUps = [];

    private ZoneWindows _members = new();
    private IReadOnlyList<ZoneDefinition> _definitions = [];
    private IReadOnlyList<PlacedZone> _placed = [];
    private WinEventHook? _winEvents;
    private IDisposable? _mouseLease;
    private IDisposable? _keyboardLease;
    private OverlayWindow? _overlay;
    private DragSession? _drag;
    private FlashState? _flash;
    private IDisposable? _flashTimer;
    private volatile Options _options;
    private volatile bool _dragActive;
    private volatile bool _foregroundInStack;
    private int _updatePending;

    private WindowZonesEngine(IWindowService windows, IMonitorService monitors, WindowZonesData data, IZoneTabStrip tabStrip,
        LowLevelMouseHook? mouseHook, LowLevelKeyboardHook? keyboardHook, ILogger logger, Options options)
    {
        _windows = windows;
        _monitors = monitors;
        _data = data;
        _tabStrip = tabStrip;
        _mouseHook = mouseHook;
        _keyboardHook = keyboardHook;
        _logger = logger;
        _options = options;
        _thread = new MessageLoopThread("Helm.WindowZones", logger);
    }

    /// <summary>Raised on the engine thread with every zone and its windows whenever that changes.</summary>
    public event Action<IReadOnlyList<ZoneSnapshot>>? ZonesChanged;

    public static async Task<WindowZonesEngine> StartAsync(IWindowService windows, IMonitorService monitors, WindowZonesData data,
        IZoneTabStrip tabStrip, LowLevelMouseHook? mouseHook, LowLevelKeyboardHook? keyboardHook, ILogger logger, Options options,
        Action<IReadOnlyList<ZoneSnapshot>> onZonesChanged)
    {
        var engine = new WindowZonesEngine(windows, monitors, data, tabStrip, mouseHook, keyboardHook, logger, options);
        engine.ZonesChanged += onZonesChanged;
        try
        {
            engine._winEvents = await WinEventHook.InstallAsync(engine._thread,
            [
                (WinEvents.SystemForeground, WinEvents.SystemForeground),
                (WinEvents.SystemMoveSizeStart, WinEvents.SystemMoveSizeEnd),
                (WinEvents.SystemMinimizeStart, WinEvents.SystemMinimizeEnd),
                (WinEvents.ObjectDestroy, WinEvents.ObjectDestroy),
            ], engine.OnWinEvent, logger).ConfigureAwait(false);

            // Null only in tests: no system-wide hooks there.
            if (mouseHook is not null)
            {
                mouseHook.Intercept += engine.OnMouse;
                engine._mouseLease = mouseHook.Acquire();
            }
            if (keyboardHook is not null)
            {
                keyboardHook.Intercept += engine.OnKey;
                engine._keyboardLease = keyboardHook.Acquire();
            }
            engine._data.Changed += engine.OnDataChanged;
            await engine._thread.InvokeAsync(engine.Load).ConfigureAwait(false);
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return engine;
    }

    public void UpdateOptions(Options options)
    {
        _options = options;
        _thread.Post(() =>
        {
            if (_overlay is not null) _overlay.Alpha = options.OverlayAlpha;
            _overlay?.Invalidate();
        });
    }

    /// <summary>The zone shortcut (any thread): open, bring to the front or close zone <paramref name="number"/>.</summary>
    public void ToggleZone(int number) => _thread.Post(() =>
    {
        RefreshZones();
        if (_placed.FirstOrDefault(z => z.Zone.Number == number) is { } zone) Toggle(zone);
        else _logger.LogInformation("No zone {Number} on the connected monitors", number);
    });

    /// <summary>From the content page: bring the zone's windows back and put its top window in front.</summary>
    public void OpenZone(string zoneId) => OnZone(zoneId, zone =>
    {
        if (_members.Members(zone.Id).Count == 0) Flash(zone, EmptyText(zone));
        else Open(zone, _members.Top(zone.Id));
    });

    /// <summary>From the content page: minimize every window of the zone.</summary>
    public void CloseZone(string zoneId) => OnZone(zoneId, Close);

    /// <summary>From the content page: show where a zone is.</summary>
    public void ShowZone(string zoneId) => OnZone(zoneId, zone => Flash(zone, zone.Zone.Label));

    /// <summary>From the content page: put one window of a zone in front.</summary>
    public void ShowWindow(nint handle) => _thread.Post(() =>
    {
        RefreshZones();
        if (_members.ZoneOf(handle) is not { } zoneId || _placed.FirstOrDefault(z => z.Id == zoneId) is not { } zone) return;
        Open(zone, handle, restoreOthers: false);
    });

    /// <summary>From the content page: take a window out of its zone (it stays where it is).</summary>
    public void Release(nint handle) => _thread.Post(() =>
    {
        if (_members.Remove(handle)) Changed();
    });

    public void RequestSnapshot() => _thread.Post(Notify);

    /// <summary>What the next/previous window keys do, without the key handling (tests).</summary>
    internal void SwitchWindow(int delta) => _thread.Post(() => Cycle(delta));

    /// <summary>Completes once everything posted before it has run (tests).</summary>
    internal Task IdleAsync() => _thread.InvokeAsync(() => { });

    public async ValueTask DisposeAsync()
    {
        if (_mouseHook is not null) _mouseHook.Intercept -= OnMouse;
        if (_keyboardHook is not null) _keyboardHook.Intercept -= OnKey;
        _data.Changed -= OnDataChanged;
        _mouseLease?.Dispose();
        _keyboardLease?.Dispose();
        _dragActive = false;
        try
        {
            await _thread.InvokeAsync(() =>
            {
                _winEvents?.Dispose();
                _flashTimer?.Dispose();
                _overlay?.Dispose();
                _overlay = null;
                _drag = null;
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        _tabStrip.Hide();
        _thread.Dispose();
    }

    // ───────────────────────────── hook callbacks (hook threads — keep tiny) ─────────────────────────────

    private void OnMouse(object? sender, MouseHookEventArgs e)
    {
        if (!_dragActive || e.Message != MouseMessage.Move) return;
        RequestUpdate();
    }

    private void OnKey(object? sender, KeyboardHookEventArgs e)
    {
        if (e.ExtraInfo == InputSimulator.HelmSignature) return;

        if (_dragActive && VirtualKeyNames.IsModifier(e.VirtualKey))
        {
            RequestUpdate();
            return;
        }

        if (!e.IsKeyDown)
        {
            if (_swallowedKeyUps.Remove(e.VirtualKey)) e.Handled = true;
            return;
        }

        // Only while the window in front shares its zone with another one; otherwise the key goes to the app.
        if (!_foregroundInStack || HotkeyRecording.IsRecording) return;
        var options = _options;
        var delta = Matches(options.NextWindow, e.VirtualKey) ? 1 : Matches(options.PreviousWindow, e.VirtualKey) ? -1 : 0;
        if (delta == 0) return;

        e.Handled = true;
        _swallowedKeyUps.Add(e.VirtualKey);
        _thread.Post(() =>
        {
            // Keeps the Start menu closed when Win is released, and makes Helm the last input source so it may
            // move the foreground to another window.
            InputSimulator.SendDummyKey();
            Cycle(delta);
        });
    }

    private bool Matches(HotkeyGesture gesture, int virtualKey)
    {
        if (gesture.IsEmpty || gesture.Key != virtualKey) return false;
        var held = HotkeyModifiers.None;
        if (_windows.IsKeyDown(VirtualKeyNames.Control)) held |= HotkeyModifiers.Ctrl;
        if (_windows.IsKeyDown(VirtualKeyNames.Menu)) held |= HotkeyModifiers.Alt;
        if (_windows.IsKeyDown(VirtualKeyNames.Shift)) held |= HotkeyModifiers.Shift;
        if (_windows.IsKeyDown(VirtualKeyNames.LWin) || _windows.IsKeyDown(VirtualKeyNames.RWin)) held |= HotkeyModifiers.Win;
        return held == gesture.Modifiers;
    }

    private void RequestUpdate()
    {
        if (Interlocked.Exchange(ref _updatePending, 1) == 0)
            _thread.Post(() =>
            {
                Volatile.Write(ref _updatePending, 0);
                UpdateDrag();
            });
    }

    // ───────────────────────────── engine thread ─────────────────────────────

    private void Load()
    {
        RefreshZones();
        var ids = _definitions.Select(z => z.Id).ToHashSet(StringComparer.Ordinal);
        _members = ZoneWindows.Import(_data.LoadWindows(), ids, (handle, pid) => _windows.GetInfo(handle) is { } info && info.ProcessId == pid);
        RefreshForeground();
        Notify();
    }

    private void OnDataChanged(object? sender, EventArgs e) => _thread.Post(() =>
    {
        var before = _placed.ToDictionary(z => z.Id, z => z.Rect, StringComparer.Ordinal);
        RefreshZones();
        var ids = _definitions.Select(z => z.Id).ToHashSet(StringComparer.Ordinal);
        if (_members.RetainZones(ids)) Save();

        // A zone that moved or changed size takes its windows with it.
        foreach (var zone in _placed)
        {
            if (before.TryGetValue(zone.Id, out var old) && old == zone.Rect) continue;
            foreach (var m in _members.Members(zone.Id))
                if (!_windows.IsMinimized(m.Handle) && !_windows.IsMaximized(m.Handle)) _windows.MoveWindow(m.Handle, zone.Rect);
        }
        RefreshForeground();
        Notify();
    });

    private void OnWinEvent(WinEventArgs e)
    {
        if (!e.IsWindowEvent) return;
        switch (e.Event)
        {
            case WinEvents.SystemMoveSizeStart:
                BeginDrag(e.Hwnd);
                break;
            case WinEvents.SystemMoveSizeEnd:
                EndDrag(e.Hwnd);
                break;
            case WinEvents.SystemForeground:
                if (_members.Touch(e.Hwnd)) Changed();
                RefreshForeground();
                break;
            case WinEvents.SystemMinimizeStart:
            case WinEvents.SystemMinimizeEnd:
                if (_members.ZoneOf(e.Hwnd) is not null) Notify();
                break;
            case WinEvents.ObjectDestroy:
                if (_members.Remove(e.Hwnd))
                {
                    Changed();
                    RefreshForeground();
                }
                break;
        }
    }

    private void RefreshZones()
    {
        _definitions = _data.GetZones();
        _placed = WindowZonesData.Place(_definitions, _monitors.GetMonitors());
    }

    private void RefreshForeground()
    {
        var zoneId = _members.ZoneOf(_windows.GetForegroundWindow());
        _foregroundInStack = zoneId is not null && _members.Members(zoneId).Count >= 2;
    }

    private bool IsEligible(nint hwnd)
    {
        if (!_windows.IsManageable(hwnd)) return false;
        return !_options.Exclusions.IsExcluded(_windows.GetProcessName(hwnd));
    }

    private void OnZone(string zoneId, Action<PlacedZone> action) => _thread.Post(() =>
    {
        RefreshZones();
        if (_placed.FirstOrDefault(z => z.Id == zoneId) is { } zone) action(zone);
    });

    /// <summary>Drops windows that closed, and those moved away from the zone some other way (Win+arrow, another tool).</summary>
    private IReadOnlyList<ZoneMember> Validate(PlacedZone zone)
    {
        var changed = false;
        foreach (var m in _members.Members(zone.Id).ToList())
        {
            var gone = _windows.GetInfo(m.Handle) is not { } info || info.ProcessId != m.ProcessId;
            var left = !gone && !_windows.IsMinimized(m.Handle) && !_windows.IsMaximized(m.Handle)
                       && _windows.GetFrameBounds(m.Handle) is { } frame && !ZoneGeometry.IsInZone(frame, zone.Rect);
            if (!gone && !left) continue;
            _members.Remove(m.Handle);
            changed = true;
        }
        if (changed) Changed();
        return _members.Members(zone.Id);
    }

    private void Toggle(PlacedZone zone)
    {
        var members = Validate(zone);
        var foreground = _windows.GetForegroundWindow();
        var minimized = members.Count(m => _windows.IsMinimized(m.Handle));
        var inFront = _members.ZoneOf(foreground) == zone.Id && !_windows.IsMinimized(foreground);
        var action = ZoneWindows.Decide(members.Count, minimized, inFront);
        _logger.LogInformation("Zone {Number}: {Action} ({Count} windows)", zone.Zone.Number, action, members.Count);
        switch (action)
        {
            case ZoneToggleAction.Empty:
                Flash(zone, EmptyText(zone));
                break;
            case ZoneToggleAction.Open:
                Open(zone, _members.Top(zone.Id));
                break;
            case ZoneToggleAction.BringToFront:
                Open(zone, _members.Top(zone.Id), restoreOthers: false);
                break;
            case ZoneToggleAction.Close:
                Close(zone);
                break;
        }
    }

    /// <summary>Brings the zone's windows back where the zone is (all of them, or just <paramref name="top"/>) and puts <paramref name="top"/> in front.</summary>
    private void Open(PlacedZone zone, nint top, bool restoreOthers = true)
    {
        var members = Validate(zone);
        if (members.Count == 0) return;
        if (members.All(m => m.Handle != top)) top = members[^1].Handle;
        if (restoreOthers)
            foreach (var m in members.Where(m => m.Handle != top))
                Fit(m.Handle, zone);
        Fit(top, zone);
        if (!_windows.ForceActivate(top)) _logger.LogDebug("Could not bring 0x{Hwnd:X} to the front", top);
        _members.Touch(top);
        ShowTabs(zone, top, onlyWhenSeveral: true);
        Changed();
    }

    private void Close(PlacedZone zone)
    {
        foreach (var m in Validate(zone)) _windows.Minimize(m.Handle);
        _tabStrip.Hide();
        Notify();
    }

    /// <summary>Restores a minimized window without stealing the focus and puts it exactly on the zone.</summary>
    private void Fit(nint handle, PlacedZone zone)
    {
        _windows.RestoreWithoutActivating(handle);
        if (_windows.IsMaximized(handle)) return; // maximized on purpose: leave it, it is still in the zone
        if (_windows.GetFrameBounds(handle) is { } frame && frame == zone.Rect) return;
        _windows.MoveWindow(handle, zone.Rect);
    }

    /// <summary>Win+PgDn / Win+PgUp: the next or previous window of the zone the window in front is in.</summary>
    private void Cycle(int delta)
    {
        var foreground = _windows.GetForegroundWindow();
        if (_members.ZoneOf(foreground) is not { } zoneId) return;
        RefreshZones();
        if (_placed.FirstOrDefault(z => z.Id == zoneId) is not { } zone) return;
        var handles = Validate(zone).Select(m => m.Handle).ToList();
        var next = ZoneGeometry.CycleTarget(handles, foreground, delta);
        if (next == 0) return;
        Fit(next, zone);
        if (!_windows.ForceActivate(next)) _logger.LogDebug("Could not bring 0x{Hwnd:X} to the front", next);
        _members.Touch(next);
        ShowTabs(zone, next, onlyWhenSeveral: false);
        Changed();
    }

    private void ShowTabs(PlacedZone zone, nint current, bool onlyWhenSeveral)
    {
        if (!_options.ShowTabStrip) return;
        var windows = Describe(zone.Id);
        if (windows.Count == 0 || (onlyWhenSeveral && windows.Count < 2)) return;
        _tabStrip.Show(new TabStripView(zone.Zone.Label, zone.Rect, zone.Monitor, windows, current));
    }

    // ───────────────────────────── dragging ─────────────────────────────

    private void BeginDrag(nint hwnd)
    {
        _drag = null;
        if (!IsEligible(hwnd) || _windows.GetFrameBounds(hwnd) is not { } frame) return;

        var cursor = _windows.GetCursorPosition();
        var scale = _monitors.FromPoint(cursor)?.Scale ?? 1;
        // A move starts on the caption band; anything else (edges, corners) is a resize.
        var captionBand = (int)(48 * scale);
        var edge = (int)(8 * scale);
        var isMove = cursor.Y >= frame.Top && cursor.Y <= frame.Top + captionBand
                     && cursor.X > frame.Left + edge && cursor.X < frame.Right - edge;
        if (!isMove)
        {
            _drag = new DragSession(hwnd, (frame.Width, frame.Height), isResize: true);
            return;
        }

        (int Width, int Height) original = (frame.Width, frame.Height);
        var member = _members.Find(hwnd);
        if (member is { OriginalWidth: > 0, OriginalHeight: > 0 }) original = (member.OriginalWidth, member.OriginalHeight);

        RefreshZones();
        // A click on the title bar starts a move too: a window in a zone leaves it only once it is really dragged away.
        _drag = new DragSession(hwnd, original, isResize: false) { Start = cursor, Leaving = member };
        _dragActive = true;
        UpdateDrag();
    }

    /// <summary>The window was dragged away from its zone: it leaves the zone, and gets its own size back.</summary>
    private void Leave(DragSession drag, PixelPoint cursor)
    {
        drag.Leaving = null;
        if (!_members.Remove(drag.Hwnd)) return;
        Changed();
        RefreshForeground();
        if (!_options.RestoreSizeOnLeave || _windows.IsMaximized(drag.Hwnd) || _windows.GetFrameBounds(drag.Hwnd) is not { } frame) return;
        var (width, height) = drag.OriginalSize;
        if (width == frame.Width && height == frame.Height) return;
        // Keep the grabbed point of the title bar under the cursor.
        var ratio = frame.Width == 0 ? 0.5 : (cursor.X - frame.Left) / (double)frame.Width;
        var left = cursor.X - (int)(ratio * width);
        _windows.MoveWindow(drag.Hwnd, PixelRect.FromSize(left, frame.Top, width, height));
    }

    private void UpdateDrag()
    {
        if (_drag is not { IsResize: false } drag) return;
        var cursor = _windows.GetCursorPosition();
        if (drag.Leaving is not null)
        {
            var reach = (int)Math.Round(LeaveDistance * (_monitors.FromPoint(cursor)?.Scale ?? 1));
            if (Math.Abs(cursor.X - drag.Start.X) > reach || Math.Abs(cursor.Y - drag.Start.Y) > reach) Leave(drag, cursor);
        }
        var active = _windows.IsKeyDown(_options.DragVirtualKey);
        var monitor = active ? _monitors.FromPoint(cursor) : null;
        var zones = monitor is null ? [] : _placed.Where(z => string.Equals(z.Monitor.Id, monitor.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (monitor is null || zones.Count == 0)
        {
            drag.Zones = [];
            drag.Hit = -1;
            if (_flash is null) _overlay?.Hide();
            return;
        }

        var hit = ZoneGeometry.HitTest(zones.Select(z => z.Rect).ToList(), cursor, (int)Math.Round(20 * monitor.Scale));
        var changed = hit != drag.Hit || drag.Monitor?.Id != monitor.Id;
        drag.Zones = zones;
        drag.Monitor = monitor;
        drag.Hit = hit;
        CancelFlash();
        ShowOverlay(monitor.WorkArea, changed);
    }

    private void EndDrag(nint hwnd)
    {
        var drag = _drag;
        _drag = null;
        _dragActive = false;
        if (_flash is null) _overlay?.Hide();
        if (drag is null || drag.Hwnd != hwnd) return;

        if (drag.IsResize)
        {
            // Resized by its edges: still in the zone only while it still fits it.
            if (_members.ZoneOf(hwnd) is { } zoneId && _placed.FirstOrDefault(z => z.Id == zoneId) is { } zone
                && _windows.GetFrameBounds(hwnd) is { } frame && !SameRect(frame, zone.Rect))
            {
                _members.Remove(hwnd);
                Changed();
                RefreshForeground();
            }
            return;
        }

        if (drag.Hit < 0 || drag.Hit >= drag.Zones.Count) return;
        var target = drag.Zones[drag.Hit];
        if (!_windows.MoveWindow(hwnd, target.Rect))
        {
            _logger.LogDebug("Could not move 0x{Hwnd:X} into zone {Zone}", hwnd, target.Zone.Label);
            return;
        }
        var pid = _windows.GetInfo(hwnd)?.ProcessId ?? 0;
        _members.Add(target.Id, new ZoneMember(hwnd, pid, drag.OriginalSize.Width, drag.OriginalSize.Height));
        _logger.LogInformation("Dropped {Process} into zone {Zone}", _windows.GetProcessName(hwnd), target.Zone.Label);
        Changed();
        RefreshForeground();
        ShowTabs(target, hwnd, onlyWhenSeveral: true);
    }

    private static bool SameRect(PixelRect a, PixelRect b) =>
        Math.Abs(a.Left - b.Left) <= ZoneGeometry.InZoneTolerance && Math.Abs(a.Top - b.Top) <= ZoneGeometry.InZoneTolerance
        && Math.Abs(a.Right - b.Right) <= ZoneGeometry.InZoneTolerance && Math.Abs(a.Bottom - b.Bottom) <= ZoneGeometry.InZoneTolerance;

    // ───────────────────────────── snapshots ─────────────────────────────

    /// <summary>Membership changed: save it and tell the pages.</summary>
    private void Changed()
    {
        Save();
        Notify();
    }

    private void Save()
    {
        try
        {
            _data.SaveWindows(_members.Export());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the windows of the zones");
        }
    }

    private void Notify()
    {
        var connected = _placed.ToDictionary(z => z.Id, StringComparer.Ordinal);
        var snapshot = _definitions
            .OrderBy(z => z.Number ?? int.MaxValue)
            .Select(z => new ZoneSnapshot(
                z.Id,
                z.Number,
                z.Name,
                z.Label,
                connected.TryGetValue(z.Id, out var placed) ? placed.Monitor.DisplayName : "Monitor not connected",
                connected.ContainsKey(z.Id),
                Describe(z.Id)))
            .ToList();
        try
        {
            ZonesChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A zones listener failed");
        }
    }

    private List<ZoneWindowSnapshot> Describe(string zoneId)
    {
        var top = _members.Top(zoneId);
        return _members.Members(zoneId)
            .Where(m => _windows.IsWindow(m.Handle))
            .Select(m => new ZoneWindowSnapshot(m.Handle, _windows.GetTitle(m.Handle), _windows.GetProcessName(m.Handle),
                _windows.GetProcessPath(m.Handle), _windows.IsMinimized(m.Handle), m.Handle == top))
            .ToList();
    }

    // ───────────────────────────── overlay ─────────────────────────────

    private string EmptyText(PlacedZone zone) =>
        $"{zone.Zone.Label} is empty: hold {_options.DragKeyName} while dragging a window here";

    private void Flash(PlacedZone zone, string label)
    {
        CancelFlash();
        _flash = new FlashState(zone, label);
        ShowOverlay(zone.Monitor.WorkArea, true);
        _flashTimer = _thread.StartTimer(FlashDuration, () =>
        {
            CancelFlash();
            if (_drag is null) _overlay?.Hide();
        });
    }

    private void CancelFlash()
    {
        if (_flash is null) return;
        _flash = null;
        _flashTimer?.Dispose();
        _flashTimer = null;
    }

    private void ShowOverlay(PixelRect bounds, bool repaint)
    {
        if (_overlay is null)
        {
            try
            {
                _overlay = OverlayWindow.Create(Paint, _options.OverlayAlpha, "Helm window zones", _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not create the zone overlay");
                return;
            }
        }

        if (_overlay.Bounds != bounds)
        {
            _overlay.SetBounds(bounds);
            repaint = true;
        }
        _overlay.Show();
        if (repaint) _overlay.Invalidate();
    }

    private void Paint(IOverlayCanvas canvas)
    {
        var o = _options;
        if (_flash is { } flash)
        {
            var origin = flash.Zone.Monitor.WorkArea;
            var scale = flash.Zone.Monitor.Scale;
            var r = flash.Zone.Rect.Offset(-origin.Left, -origin.Top);
            canvas.Fill(r, o.HighlightColor);
            canvas.Frame(r, o.BorderColor, Math.Max(2, (int)Math.Round(3 * scale)));
            canvas.Text(r, flash.Label, o.BorderColor, (int)Math.Round(Math.Clamp(r.Height / 14.0, 16 * scale, 28 * scale)));
            return;
        }

        if (_drag is not { Monitor: { } monitor } drag) return;
        var workArea = monitor.WorkArea;
        var s = monitor.Scale;
        var border = Math.Max(1, (int)Math.Round(2 * s));
        for (var i = 0; i < drag.Zones.Count; i++)
        {
            var zone = drag.Zones[i];
            var r = zone.Rect.Offset(-workArea.Left, -workArea.Top);
            var highlighted = i == drag.Hit;
            canvas.Fill(r, highlighted ? o.HighlightColor : o.ZoneColor);
            canvas.Frame(r, highlighted ? o.HighlightColor.Lerp(o.BorderColor, 0.5) : o.BorderColor, border);
            if (!o.ShowZoneNumbers) continue;

            var count = _members.Members(zone.Id).Count;
            var big = (int)Math.Round(Math.Clamp(Math.Min(r.Width, r.Height) / 5.0, 18 * s, 56 * s));
            var number = zone.Zone.Number is { } n ? n.ToString() : "·";
            var mid = r.Top + r.Height / 2;
            canvas.Text(new PixelRect(r.Left, mid - big, r.Right, mid + big / 4), number, o.BorderColor, big);
            var detail = string.IsNullOrWhiteSpace(zone.Zone.Name) ? string.Empty : zone.Zone.Name.Trim();
            if (count > 0) detail = detail.Length == 0 ? Windows(count) : $"{detail} · {Windows(count)}";
            if (detail.Length > 0)
                canvas.Text(new PixelRect(r.Left + 8, mid + big / 4, r.Right - 8, mid + big / 4 + (int)(30 * s)), detail, o.BorderColor, (int)Math.Round(15 * s), bold: false);
        }
    }

    private static string Windows(int count) => count == 1 ? "1 window" : $"{count} windows";

    internal sealed record Options(
        int DragVirtualKey,
        string DragKeyName,
        HotkeyGesture NextWindow,
        HotkeyGesture PreviousWindow,
        bool RestoreSizeOnLeave,
        bool ShowTabStrip,
        byte OverlayAlpha,
        RgbColor ZoneColor,
        RgbColor BorderColor,
        RgbColor HighlightColor,
        bool ShowZoneNumbers,
        ProcessExclusions Exclusions)
    {
        public static Options From(WindowZonesSettings s) => new(
            s.DragVirtualKey,
            s.DragKey.ToString(),
            s.NextWindowHotkey,
            s.PreviousWindowHotkey,
            s.RestoreSizeOnLeave,
            s.ShowTabStrip,
            (byte)Math.Clamp(Math.Clamp(s.ZoneOpacity, WindowZonesSettings.MinOpacity, WindowZonesSettings.MaxOpacity) * 255 / 100, 20, 255),
            new RgbColor(43, 43, 43),
            new RgbColor(255, 255, 255),
            AvoidKey(AccentColor.Current),
            s.ShowZoneNumbers,
            new ProcessExclusions(s.ExcludedApps));

        /// <summary>The overlay's color key would make that exact color invisible; nudge it.</summary>
        private static RgbColor AvoidKey(RgbColor c) => c == OverlayWindow.TransparentKey ? new RgbColor(2, 0, 2) : c;
    }

    private sealed record FlashState(PlacedZone Zone, string Label);

    private sealed class DragSession(nint hwnd, (int Width, int Height) originalSize, bool isResize)
    {
        public nint Hwnd { get; } = hwnd;
        public (int Width, int Height) OriginalSize { get; } = originalSize;
        public bool IsResize { get; } = isResize;
        public PixelPoint Start { get; init; }

        /// <summary>The window is in a zone and has not been dragged far enough to leave it yet.</summary>
        public ZoneMember? Leaving { get; set; }
        public MonitorInfo? Monitor { get; set; }
        public IReadOnlyList<PlacedZone> Zones { get; set; } = [];
        public int Hit { get; set; } = -1;
    }
}
