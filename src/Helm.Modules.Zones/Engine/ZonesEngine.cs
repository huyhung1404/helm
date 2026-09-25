using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Interop;
using Helm.Modules.Zones.Layouts;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Zones.Engine;

/// <summary>
/// The snapping engine. Everything stateful runs on one <see cref="MessageLoopThread"/> ("Helm.Zones"): WinEvents
/// arrive there, the GDI overlay lives there, and hook callbacks only post small work items to it. Hook callbacks
/// themselves read only volatile snapshots so they return within microseconds.
/// </summary>
internal sealed class ZonesEngine : IAsyncDisposable
{
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly ZonesDataService _data;
    private readonly LowLevelMouseHook _mouseHook;
    private readonly LowLevelKeyboardHook _keyboardHook;
    private readonly ILogger _logger;
    private readonly MessageLoopThread _thread;
    private readonly Dictionary<nint, SnapState> _snapped = new();
    private readonly HashSet<nint> _placedNewWindows = [];
    private readonly Dictionary<string, MonitorZones> _zoneCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _swallowedKeyUps = [];

    private WinEventHook? _winEvents;
    private IDisposable? _mouseLease;
    private IDisposable? _keyboardLease;
    private OverlayWindow? _overlay;
    private DragSession? _drag;
    private volatile Options _options;
    private volatile bool _dragActive;
    private volatile bool _foregroundSnappable;
    private volatile bool _foregroundVertical;
    private int _updatePending;

    private ZonesEngine(IWindowService windows, IMonitorService monitors, ZonesDataService data,
        LowLevelMouseHook mouseHook, LowLevelKeyboardHook keyboardHook, ILogger logger, Options options)
    {
        _windows = windows;
        _monitors = monitors;
        _data = data;
        _mouseHook = mouseHook;
        _keyboardHook = keyboardHook;
        _logger = logger;
        _options = options;
        _thread = new MessageLoopThread("Helm.Zones", logger);
    }

    public static async Task<ZonesEngine> StartAsync(IWindowService windows, IMonitorService monitors, ZonesDataService data,
        LowLevelMouseHook mouseHook, LowLevelKeyboardHook keyboardHook, ILogger logger, Options options)
    {
        var engine = new ZonesEngine(windows, monitors, data, mouseHook, keyboardHook, logger, options);
        try
        {
            engine._winEvents = await WinEventHook.InstallAsync(engine._thread,
            [
                (WinEvents.SystemForeground, WinEvents.SystemForeground),
                (WinEvents.SystemMoveSizeStart, WinEvents.SystemMoveSizeEnd),
                (WinEvents.ObjectDestroy, WinEvents.ObjectShow),
            ], engine.OnWinEvent, logger).ConfigureAwait(false);

            engine._mouseHook.Intercept += engine.OnMouse;
            engine._keyboardHook.Intercept += engine.OnKey;
            engine._mouseLease = mouseHook.Acquire();
            engine._keyboardLease = keyboardHook.Acquire();
            engine._data.Changed += engine.OnDataChanged;
            await engine._thread.InvokeAsync(engine.RefreshForeground).ConfigureAwait(false);
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
            _overlay?.Invalidate();
            if (_overlay is not null) _overlay.Alpha = options.OverlayAlpha;
            RefreshForeground();
        });
    }

    public async ValueTask DisposeAsync()
    {
        _mouseHook.Intercept -= OnMouse;
        _keyboardHook.Intercept -= OnKey;
        _data.Changed -= OnDataChanged;
        _mouseLease?.Dispose();
        _keyboardLease?.Dispose();
        _dragActive = false;
        try
        {
            await _thread.InvokeAsync(() =>
            {
                _winEvents?.Dispose();
                _overlay?.Dispose();
                _overlay = null;
                _drag = null;
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
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

        var options = _options;
        if (!options.OverrideWindowsSnap || HotkeyRecording.IsRecording || !_foregroundSnappable) return;
        var delta = e.VirtualKey switch
        {
            VirtualKeyNames.Left => -1,
            VirtualKeyNames.Right => 1,
            VirtualKeyNames.Up when _foregroundVertical => -1,
            VirtualKeyNames.Down when _foregroundVertical => 1,
            _ => 0,
        };
        if (delta == 0) return;
        if (!(_windows.IsKeyDown(VirtualKeyNames.LWin) || _windows.IsKeyDown(VirtualKeyNames.RWin))) return;
        if (_windows.IsKeyDown(VirtualKeyNames.Control) || _windows.IsKeyDown(VirtualKeyNames.Menu) || _windows.IsKeyDown(VirtualKeyNames.Shift)) return;

        e.Handled = true;
        _swallowedKeyUps.Add(e.VirtualKey);
        _thread.Post(() =>
        {
            InputSimulator.SendDummyKey(); // keeps the Start menu closed when Win is released
            MoveForegroundByKeyboard(delta);
        });
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

    private void OnDataChanged(object? sender, EventArgs e) => _thread.Post(() =>
    {
        _zoneCache.Clear();
        RefreshForeground();
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
                RefreshForeground();
                break;
            case WinEvents.ObjectShow:
                if (_options.MoveNewWindowsToLastZone) PlaceNewWindow(e.Hwnd);
                break;
            case WinEvents.ObjectDestroy:
                _snapped.Remove(e.Hwnd);
                _placedNewWindows.Remove(e.Hwnd);
                break;
        }
    }

    private bool IsEligible(nint hwnd)
    {
        if (!_windows.IsManageable(hwnd)) return false;
        return !_options.Exclusions.IsExcluded(_windows.GetProcessName(hwnd));
    }

    private void RefreshForeground()
    {
        var hwnd = _windows.GetForegroundWindow();
        var eligible = hwnd != 0 && IsEligible(hwnd);
        _foregroundSnappable = eligible;
        if (eligible && _monitors.FromWindow(hwnd) is { } monitor)
            _foregroundVertical = ZoneMath.IsVerticalStack(ZonesFor(monitor).Layout.GetZones());
        else
            _foregroundVertical = false;
    }

    private MonitorZones ZonesFor(MonitorInfo monitor)
    {
        var key = $"{monitor.Id}|{monitor.WorkArea}|{monitor.Dpi}";
        if (!_zoneCache.TryGetValue(key, out var zones))
        {
            zones = _data.GetZones(monitor);
            _zoneCache[key] = zones;
        }
        return zones;
    }

    private void BeginDrag(nint hwnd)
    {
        _drag = null;
        if (!IsEligible(hwnd) || _windows.GetFrameBounds(hwnd) is not { } frame) return;

        var cursor = _windows.GetCursorPosition();
        var scale = _monitors.FromPoint(cursor)?.Scale ?? 1;
        // A move starts on the caption band; anything else (edges, corners) is a resize we leave alone.
        var captionBand = (int)(48 * scale);
        var edge = (int)(8 * scale);
        var isMove = cursor.Y >= frame.Top && cursor.Y <= frame.Top + captionBand
                     && cursor.X > frame.Left + edge && cursor.X < frame.Right - edge;
        if (!isMove) return;

        (int Width, int Height)? originalSize = null;
        if (_snapped.Remove(hwnd, out var snap))
        {
            originalSize = snap.OriginalSize;
            if (_options.RestoreSizeOnUnsnap && !_windows.IsMaximized(hwnd))
            {
                // Shrink back to the pre-snap size, keeping the cursor at the same relative spot of the caption.
                var ratio = frame.Width == 0 ? 0.5 : (cursor.X - frame.Left) / (double)frame.Width;
                var left = cursor.X - (int)(ratio * snap.OriginalSize.Width);
                _windows.MoveWindow(hwnd, PixelRect.FromSize(left, frame.Top, snap.OriginalSize.Width, snap.OriginalSize.Height));
                frame = _windows.GetFrameBounds(hwnd) ?? frame;
            }
        }

        _drag = new DragSession(hwnd, originalSize ?? (frame.Width, frame.Height));
        _dragActive = true;
        UpdateDrag();
    }

    private void UpdateDrag()
    {
        if (_drag is not { } drag) return;
        var options = _options;
        var cursor = _windows.GetCursorPosition();
        var active = options.AlwaysShowZones ^ _windows.IsKeyDown(options.ActivationKey);
        if (!active)
        {
            drag.Selection = [];
            drag.Anchor = -1;
            _overlay?.Hide();
            return;
        }

        if (_monitors.FromPoint(cursor) is not { } monitor) return;
        var zones = ZonesFor(monitor);
        if (drag.Zones?.Monitor.Id != monitor.Id || !ReferenceEquals(drag.Zones, zones))
        {
            drag.Zones = zones;
            drag.Anchor = -1;
        }

        var hit = ZoneMath.HitTest(zones.Zones, cursor, zones.Sensitivity);
        IReadOnlyList<int> selection;
        if (hit < 0)
        {
            selection = [];
        }
        else if (options.MultiZoneSpanning && _windows.IsKeyDown(options.SpanKey) && drag.Anchor >= 0)
        {
            selection = ZoneMath.SelectSpan(zones.Zones, drag.Anchor, hit);
        }
        else
        {
            drag.Anchor = hit;
            selection = [hit];
        }

        var changed = !selection.SequenceEqual(drag.Selection);
        drag.Selection = selection;
        ShowOverlay(zones, changed);
    }

    private void EndDrag(nint hwnd)
    {
        var drag = _drag;
        _drag = null;
        _dragActive = false;
        _overlay?.Hide();
        if (drag is null || drag.Hwnd != hwnd || drag.Zones is not { } zones || drag.Selection.Count == 0) return;

        var target = ZoneMath.UnionOf(zones.Zones, drag.Selection);
        Snap(hwnd, zones, drag.Selection, target, drag.OriginalSize);
    }

    private void Snap(nint hwnd, MonitorZones zones, IReadOnlyList<int> indices, PixelRect target, (int Width, int Height) originalSize)
    {
        if (!_windows.MoveWindow(hwnd, target))
        {
            _logger.LogDebug("Could not move 0x{Hwnd:X} into zone(s) {Zones}", hwnd, string.Join(",", indices));
            return;
        }
        _snapped[hwnd] = new SnapState(zones.Monitor.Id, zones.Layout.Id, indices.ToList(), originalSize);

        var process = _windows.GetProcessName(hwnd);
        if (!string.IsNullOrEmpty(process))
        {
            _data.RecordHistory(HistoryKeys(process, _windows.GetTitle(hwnd)), new ZoneHistoryEntry
            {
                MonitorId = zones.Monitor.Id,
                LayoutId = zones.Layout.Id,
                Zones = indices.ToList(),
                Updated = DateTimeOffset.Now,
            });
        }
    }

    private void MoveForegroundByKeyboard(int delta)
    {
        var hwnd = _windows.GetForegroundWindow();
        if (!IsEligible(hwnd) || _monitors.FromWindow(hwnd) is not { } monitor) return;
        var zones = ZonesFor(monitor);
        if (zones.Zones.Count == 0) return;

        int? current = null;
        if (_snapped.TryGetValue(hwnd, out var snap) && snap.MonitorId == monitor.Id && snap.LayoutId == zones.Layout.Id)
            current = delta > 0 ? snap.Zones.Max() : snap.Zones.Min();
        else if (_windows.GetFrameBounds(hwnd) is { } frame && ZoneMath.MatchZone(zones.Zones, frame) is var matched and >= 0)
            current = matched;

        var next = ZoneMath.Step(zones.Zones.Count, current, delta);
        var original = snap?.OriginalSize ?? (_windows.GetFrameBounds(hwnd) is { } f ? (f.Width, f.Height) : (800, 600));
        Snap(hwnd, zones, [next], zones.Zones[next], original);
    }

    private void PlaceNewWindow(nint hwnd)
    {
        if (!_placedNewWindows.Add(hwnd)) return;
        if (!IsEligible(hwnd)) return;
        var process = _windows.GetProcessName(hwnd);
        if (string.IsNullOrEmpty(process)) return;
        if (_data.FindHistory(HistoryKeys(process, _windows.GetTitle(hwnd))) is not { } entry) return;

        var monitor = _monitors.GetMonitors().FirstOrDefault(m => string.Equals(m.Id, entry.MonitorId, StringComparison.OrdinalIgnoreCase));
        if (monitor is null) return;
        var zones = ZonesFor(monitor);
        if (zones.Layout.Id != entry.LayoutId || entry.Zones.Any(z => z < 0 || z >= zones.Zones.Count)) return;

        var frame = _windows.GetFrameBounds(hwnd);
        var target = ZoneMath.UnionOf(zones.Zones, entry.Zones);
        // Give the app a moment to finish its own initial placement, then snap.
        _ = Task.Delay(150).ContinueWith(_ => _thread.Post(() =>
        {
            if (!_windows.IsWindow(hwnd) || _windows.IsMaximized(hwnd) || _windows.IsMinimized(hwnd)) return;
            Snap(hwnd, zones, entry.Zones, target, frame is { } f ? (f.Width, f.Height) : (target.Width, target.Height));
            _logger.LogInformation("Moved new {Process} window into zone(s) {Zones}", process, string.Join(",", entry.Zones));
        }), TaskScheduler.Default);
    }

    /// <summary>Most specific first: process + title hash, then process only.</summary>
    private static IEnumerable<string> HistoryKeys(string process, string title)
    {
        yield return $"{process.ToLowerInvariant()}|{StableHash(title):x8}";
        yield return process.ToLowerInvariant();
    }

    private static uint StableHash(string text)
    {
        var hash = 2166136261u; // FNV-1a
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash;
    }

    private void ShowOverlay(MonitorZones zones, bool selectionChanged)
    {
        if (_overlay is null)
        {
            try
            {
                _overlay = OverlayWindow.Create(Paint, _options.OverlayAlpha, "Helm zones overlay");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not create the zone overlay");
                return;
            }
        }

        if (_overlay.Bounds != zones.Monitor.WorkArea)
        {
            _overlay.SetBounds(zones.Monitor.WorkArea);
            selectionChanged = true;
        }
        _overlay.Show();
        if (selectionChanged) _overlay.Invalidate();
    }

    private void Paint(IOverlayCanvas canvas)
    {
        if (_drag is not { Zones: { } zones } drag) return;
        var o = _options;
        var origin = zones.Monitor.WorkArea;
        var border = Math.Max(1, (int)Math.Round(2 * zones.Monitor.Scale));
        var fontSize = (int)Math.Round(40 * zones.Monitor.Scale);
        for (var i = 0; i < zones.Zones.Count; i++)
        {
            var r = zones.Zones[i].Offset(-origin.Left, -origin.Top);
            var highlighted = drag.Selection.Contains(i);
            canvas.Fill(r, highlighted ? o.HighlightColor : o.ZoneColor);
            canvas.Frame(r, highlighted ? o.HighlightColor.Lerp(o.BorderColor, 0.5) : o.BorderColor, border);
            if (o.ShowZoneNumbers) canvas.Text(r, (i + 1).ToString(), o.BorderColor, fontSize);
        }
    }

    internal sealed record Options(
        bool AlwaysShowZones,
        int ActivationKey,
        bool MultiZoneSpanning,
        int SpanKey,
        bool OverrideWindowsSnap,
        bool RestoreSizeOnUnsnap,
        bool MoveNewWindowsToLastZone,
        byte OverlayAlpha,
        RgbColor ZoneColor,
        RgbColor BorderColor,
        RgbColor HighlightColor,
        bool ShowZoneNumbers,
        ProcessExclusions Exclusions)
    {
        public static Options From(ZonesSettings s) => new(
            s.AlwaysShowZones,
            s.ActivationVirtualKey,
            s.MultiZoneSpanning,
            s.SpanKey,
            s.OverrideWindowsSnap,
            s.RestoreSizeOnUnsnap,
            s.MoveNewWindowsToLastZone,
            (byte)Math.Clamp(s.ZoneOpacity * 255 / 100, 20, 255),
            AvoidKey(RgbColor.Parse(s.ZoneColor, new RgbColor(43, 43, 43))),
            AvoidKey(RgbColor.Parse(s.BorderColor, new RgbColor(255, 255, 255))),
            AvoidKey(RgbColor.Parse(s.HighlightColor, new RgbColor(0, 120, 212))),
            s.ShowZoneNumbers,
            new ProcessExclusions(s.ExcludedApps));

        /// <summary>The overlay's color key would make that exact color invisible; nudge it.</summary>
        private static RgbColor AvoidKey(RgbColor c) => c == OverlayWindow.TransparentKey ? new RgbColor(2, 0, 2) : c;
    }

    private sealed class DragSession(nint hwnd, (int Width, int Height) originalSize)
    {
        public nint Hwnd { get; } = hwnd;
        public (int Width, int Height) OriginalSize { get; } = originalSize;
        public MonitorZones? Zones { get; set; }
        public int Anchor { get; set; } = -1;
        public IReadOnlyList<int> Selection { get; set; } = [];
    }

    private sealed record SnapState(string MonitorId, string LayoutId, List<int> Zones, (int Width, int Height) OriginalSize);
}
