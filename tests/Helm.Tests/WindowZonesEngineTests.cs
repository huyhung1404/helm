using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Settings;
using Helm.Modules.WindowZones;
using Helm.Modules.WindowZones.Engine;
using Helm.Modules.WindowZones.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The engine on its real thread, with the desktop faked: no real window is moved, minimized or activated, no global
/// keyboard or mouse hook is installed, and the only monitor sits far off every real screen.
/// </summary>
public sealed class WindowZonesEngineTests : IAsyncLifetime
{
    private static readonly MonitorInfo Screen = new(1, "FAKE_1000x800", "FAKE", new PixelRect(-20000, -20000, -19000, -19200),
        new PixelRect(-20000, -20000, -19000, -19200), 96, true);

    private readonly TempDir _dir = new();
    private readonly FakeDesktop _desktop = new();
    private readonly FakeTabStrip _tabs = new();
    private SettingsStoreFactory _settings = null!;
    private WindowZonesData _data = null!;
    private WindowZonesEngine _engine = null!;
    private IReadOnlyList<ZoneSnapshot> _zones = [];

    public async Task InitializeAsync()
    {
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
        _data = new WindowZonesData(_settings);
        _data.SaveZones([new ZoneDefinition { Id = "z", Name = "Code", MonitorId = Screen.Id, Rect = new ZoneRect(0, 0, 1, 1) }], [Screen]);

        foreach (var h in new nint[] { 101, 102, 103 }) _desktop.Add(h, pid: 7, Screen.WorkArea);
        _desktop.Add(104, pid: 8, Screen.WorkArea); // its handle was saved with another program's id
        _desktop.Foreground = 999;
        var saved = new ZoneWindows();
        foreach (var h in new nint[] { 101, 102, 103 }) saved.Add("z", new ZoneMember(h, 7, 640, 480));
        saved.Add("z", new ZoneMember(104, 9, 0, 0));
        saved.Touch(102);
        _data.SaveWindows(saved.Export());

        _engine = await WindowZonesEngine.StartAsync(_desktop, _desktop, _data, _tabs, mouseHook: null, keyboardHook: null,
            NullLogger.Instance, WindowZonesEngine.Options.From(new WindowZonesSettings()), z => _zones = z);
        await _engine.IdleAsync();
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _settings.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task Saved_windows_come_back_and_the_zone_shortcut_opens_brings_forward_and_closes()
    {
        var zone = Assert.Single(_zones);
        Assert.Equal((1, "1 · Code"), (zone.Number, zone.Label));
        Assert.Equal([101, 102, 103], zone.Windows.Select(w => (long)w.Handle)); // 104 belongs to another program now
        Assert.True(zone.Windows.Single(w => w.Handle == 102).IsTop);

        // Behind another app: the zone's top window comes to the front.
        _engine.ToggleZone(1);
        await _engine.IdleAsync();
        Assert.Equal(102, _desktop.Foreground);
        Assert.DoesNotContain(_desktop.Calls, c => c.StartsWith("min"));
        Assert.Equal(102, _tabs.Last?.Current);

        // In front: every window of the zone is minimized.
        _engine.ToggleZone(1);
        await _engine.IdleAsync();
        Assert.True(_desktop.Windows.Where(w => w.Key != 104).All(w => w.Value.Minimized));
        Assert.True(_zones[0].IsClosed);

        // Closed: all come back, the last one in front on top.
        _desktop.Calls.Clear();
        _engine.ToggleZone(1);
        await _engine.IdleAsync();
        Assert.False(_desktop.Windows[101].Minimized || _desktop.Windows[102].Minimized || _desktop.Windows[103].Minimized);
        Assert.Equal(["restore 101", "restore 103", "restore 102", "activate 102"], _desktop.Calls);
        Assert.False(_zones[0].IsClosed);
    }

    [Fact]
    public async Task Next_and_previous_go_round_the_windows_of_the_zone_in_front()
    {
        _desktop.Foreground = 102;
        _engine.SwitchWindow(1);
        await _engine.IdleAsync();
        Assert.Equal(103, _desktop.Foreground);
        Assert.Equal((103, 3), (_tabs.Last?.Current, _tabs.Last?.Windows.Count));

        _engine.SwitchWindow(1);
        await _engine.IdleAsync();
        Assert.Equal(101, _desktop.Foreground);

        _engine.SwitchWindow(-1);
        await _engine.IdleAsync();
        Assert.Equal(103, _desktop.Foreground);

        // A minimized window of the zone comes back where the zone is.
        _desktop.Windows[101].Minimized = true;
        _desktop.Windows[101].Frame = new PixelRect(-32000, -32000, -31800, -31970);
        _engine.SwitchWindow(1);
        await _engine.IdleAsync();
        Assert.Equal(101, _desktop.Foreground);
        Assert.Equal(Screen.WorkArea, _desktop.Windows[101].Frame);

        // Outside any zone the keys do nothing.
        _desktop.Foreground = 999;
        _engine.SwitchWindow(1);
        await _engine.IdleAsync();
        Assert.Equal(999, _desktop.Foreground);
    }

    [Fact]
    public async Task Windows_moved_away_or_closed_leave_the_zone_and_the_file_follows()
    {
        _desktop.Windows[102].Frame = Screen.WorkArea.Offset(300, 0); // moved some other way (Win+arrow)
        _desktop.Remove(103);                                          // closed
        _engine.ToggleZone(1);
        await _engine.IdleAsync();

        Assert.Equal([101], _zones[0].Windows.Select(w => (long)w.Handle));
        var saved = _data.LoadWindows().Zones["z"];
        Assert.Equal([101], saved.Windows.Select(w => w.Handle));
        Assert.Equal(640, saved.Windows[0].OriginalWidth);
    }

    [Fact]
    public async Task Deleting_a_zone_lets_its_windows_go_and_moving_it_takes_them_along()
    {
        var moved = new ZoneDefinition { Id = "z", Name = "Code", Number = 1, MonitorId = Screen.Id, Rect = new ZoneRect(0, 0, 0.5, 1) };
        _data.SaveZones([moved], [Screen]);
        await Task.Delay(50);
        await _engine.IdleAsync();
        var half = ZoneGeometry.ToPixels(moved.Rect, Screen.WorkArea);
        Assert.All(new nint[] { 101, 102, 103 }, h => Assert.Equal(half, _desktop.Windows[h].Frame));

        _data.SaveZones([], [Screen]);
        await Task.Delay(50);
        await _engine.IdleAsync();
        Assert.Empty(_zones);
        Assert.Empty(_data.LoadWindows().Zones);
    }

    private sealed class FakeTabStrip : IZoneTabStrip
    {
        public TabStripView? Last { get; private set; }

        public void Show(TabStripView view) => Last = view;

        public void Hide() { }
    }

    private sealed class FakeWindow(int pid, PixelRect frame)
    {
        public int Pid { get; } = pid;
        public PixelRect Frame { get; set; } = frame;
        public bool Minimized { get; set; }
    }

    /// <summary>A desktop of made-up windows on a made-up monitor. Records what the engine does to them.</summary>
    private sealed class FakeDesktop : IWindowService, IMonitorService
    {
        private readonly object _gate = new();

        public Dictionary<nint, FakeWindow> Windows { get; } = [];
        public List<string> Calls { get; } = [];
        public nint Foreground { get; set; }

        public void Add(nint h, int pid, PixelRect frame) { lock (_gate) Windows[h] = new FakeWindow(pid, frame); }
        public void Remove(nint h) { lock (_gate) Windows.Remove(h); }

        private FakeWindow? Get(nint h) { lock (_gate) return Windows.GetValueOrDefault(h); }

        public IReadOnlyList<WindowInfo> GetAppWindows() => Windows.Keys.Select(GetInfo).OfType<WindowInfo>().ToList();

        public WindowInfo? GetInfo(nint hwnd) => Get(hwnd) is { } w
            ? new WindowInfo(hwnd, $"Window {hwnd}", "Fake", w.Pid, "fake.exe", w.Frame, false, w.Minimized, false, false)
            : null;

        public bool IsAppWindow(nint hwnd) => Get(hwnd) is not null;
        public bool IsManageable(nint hwnd) => Get(hwnd) is not null;
        public nint GetForegroundWindow() => Foreground;
        public string GetProcessName(nint hwnd) => "fake.exe";
        public string GetProcessPath(nint hwnd) => string.Empty;
        public string GetTitle(nint hwnd) => $"Window {hwnd}";
        public PixelRect? GetFrameBounds(nint hwnd) => Get(hwnd)?.Frame;

        public bool MoveWindow(nint hwnd, PixelRect frame, bool activate = false)
        {
            if (Get(hwnd) is not { } w) return false;
            w.Minimized = false;
            w.Frame = frame;
            return true;
        }

        public bool SetWindowBounds(nint hwnd, PixelRect bounds) => true;
        public bool SetWindowPosition(nint hwnd, PixelPoint topLeft) => true;
        public bool SetTopmost(nint hwnd, bool topmost) => true;
        public bool SetExcludedFromCapture(nint hwnd, bool excluded) => true;
        public bool MakePassiveOverlay(nint hwnd) => true;
        public bool Activate(nint hwnd) => ForceActivate(hwnd);

        public bool Minimize(nint hwnd)
        {
            if (Get(hwnd) is not { } w) return false;
            Calls.Add($"min {hwnd}");
            w.Minimized = true;
            if (Foreground == hwnd) Foreground = 0;
            return true;
        }

        public bool RestoreWithoutActivating(nint hwnd)
        {
            if (Get(hwnd) is not { } w) return false;
            if (w.Minimized) Calls.Add($"restore {hwnd}");
            w.Minimized = false;
            return true;
        }

        public bool ForceActivate(nint hwnd)
        {
            if (Get(hwnd) is not { } w) return false;
            Calls.Add($"activate {hwnd}");
            w.Minimized = false;
            Foreground = hwnd;
            return true;
        }

        public bool IsTopmost(nint hwnd) => false;
        public bool IsWindow(nint hwnd) => Get(hwnd) is not null;
        public bool IsVisible(nint hwnd) => Get(hwnd) is not null;
        public bool IsMinimized(nint hwnd) => Get(hwnd)?.Minimized ?? false;
        public bool IsMaximized(nint hwnd) => false;
        public bool IsCloaked(nint hwnd) => false;
        public PixelPoint GetCursorPosition() => new(-19500, -19500);
        public bool IsKeyDown(int virtualKey) => false;
        public bool IsFullScreenGameRunning() => false;

        public IReadOnlyList<MonitorInfo> GetMonitors() => [Screen];
        public MonitorInfo? FromPoint(PixelPoint point) => Screen.Bounds.Contains(point) ? Screen : null;
        public MonitorInfo? FromWindow(nint hwnd) => Get(hwnd) is not null ? Screen : null;
        public MonitorInfo? FromRect(PixelRect rect) => Screen;
    }
}
