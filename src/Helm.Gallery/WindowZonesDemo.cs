using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Modules.WindowZones;
using Helm.Modules.WindowZones.Engine;
using Helm.Modules.WindowZones.Model;
using Helm.Modules.WindowZones.Ui;

namespace Helm.Gallery;

/// <summary>Which Window Zones a Gallery entry shows.</summary>
internal enum WindowZonesStage
{
    /// <summary>Two screens with seven zones (one on a laptop screen not connected now), windows in most of them.</summary>
    Zones,
    /// <summary>The zone editor sheet, a zone chosen.</summary>
    Editor,
    /// <summary>Two zone shortcuts taken by another app.</summary>
    ShortcutTaken,
    /// <summary>No zone yet.</summary>
    FirstRun,
    /// <summary>The settings sheet (the gear).</summary>
    Settings,
    /// <summary>The settings sheet scrolled to its end.</summary>
    SettingsEnd,
}

/// <summary>Which full-screen editor a Gallery entry shows.</summary>
internal enum ZoneScreenStage
{
    /// <summary>The full-screen editor on the monitor it was opened on: the toolbar, a zone chosen.</summary>
    Toolbar,
    /// <summary>The full-screen editor on the other monitor: where the toolbar is, a zone chosen.</summary>
    OtherMonitor,
}

/// <summary>
/// The real Window Zones view and view model over zones in a temporary folder and a pretend engine: two pretend monitors,
/// pretend windows. Nothing touches a real window: opening, closing and taking out only change the pretend list.
/// </summary>
internal static class WindowZonesDemo
{
    public static Control Create(FakeModule module, WindowZonesStage stage) => Build(module, stage).View;

    /// <summary>The full-screen editor (one monitor's window content), over the same pretend zones.</summary>
    public static Control Screen(FakeModule module, ZoneScreenStage stage) => ScreenEditor(module, stage);

    /// <summary>The editor's view for one pretend monitor, its session shared as on the real screens.</summary>
    public static ZoneScreenEditorView ScreenEditor(FakeModule module, ZoneScreenStage stage)
    {
        var (_, _, zones) = Build(module, WindowZonesStage.Zones);
        var session = new Helm.Modules.WindowZones.Editor.EditorSession(zones.Data, Monitors, Monitors[0]);
        // The big zone chosen on the first screen; the unnamed one on the second.
        session.Edit.Select(stage == ZoneScreenStage.Toolbar ? 0 : 5);
        return stage == ZoneScreenStage.Toolbar
            ? new ZoneScreenEditorView(session, Monitors[0], isHome: true)
            : new ZoneScreenEditorView(session, Monitors[1], isHome: false);
    }

    /// <summary>The view with its view model and pretend engine (the UI tests drive them).</summary>
    public static (WindowZonesView View, WindowZonesViewModel ViewModel, PretendZones Zones) Build(FakeModule module, WindowZonesStage stage)
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var settings = new SettingsStoreFactory(new HelmPaths(dir));
        if (stage != WindowZonesStage.FirstRun) settings.GetFile<ZonesFile>(Path.Combine("window-zones", "zones.json")).Update(f => f.Zones = Zones());
        var zones = new PretendZones(module, settings, stage == WindowZonesStage.FirstRun ? [] : Windows());
        if (stage == WindowZonesStage.ShortcutTaken)
            zones.StatusMessage = "Ctrl+Alt+4, 5 could not be registered (another app uses them). Pick other keys in the settings.";

        var vm = new WindowZonesViewModel(zones, new AvaloniaUi());
        var view = new WindowZonesView(vm, new Host());
        switch (stage)
        {
            case WindowZonesStage.Editor:
                view.AttachedToVisualTree += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    view.OpenEditor();
                    Dispatcher.UIThread.Post(() => view.Editor?.Session?.Edit.Select(1), DispatcherPriority.Background);
                }, TimeSpan.FromMilliseconds(250));
                break;
            case WindowZonesStage.Settings or WindowZonesStage.SettingsEnd:
                view.AttachedToVisualTree += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    view.OpenSettings();
                    if (stage == WindowZonesStage.SettingsEnd) Dispatcher.UIThread.Post(() => ScrollSheetToEnd(view), DispatcherPriority.Background);
                }, TimeSpan.FromMilliseconds(250));
                break;
        }
        return (view, vm, zones);
    }

    private static void ScrollSheetToEnd(Control view)
    {
        if (Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(view) is not { } layer) return;
        var settings = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(layer).OfType<WindowZonesSettingsView>().FirstOrDefault();
        if (settings is not null && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<ScrollViewer>(settings) is { } scroller) scroller.ScrollToEnd();
    }

    /// <summary>A 1440p screen with the taskbar, and a 1080p one to its right, a little lower.</summary>
    public static IReadOnlyList<MonitorInfo> Monitors { get; } =
    [
        new(1, "DISPLAY1_2560x1440", @"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1392), 120, true),
        new(2, "DISPLAY2_1920x1080", @"\\.\DISPLAY2", new PixelRect(2560, 180, 4480, 1260), new PixelRect(2560, 180, 4480, 1260), 96, false),
    ];

    private static List<ZoneDefinition> Zones() =>
    [
        new() { Id = "code", Number = 1, Name = "Code", MonitorId = "DISPLAY1_2560x1440", Rect = new ZoneRect(0, 0, 0.58, 1) },
        new() { Id = "chat", Number = 2, Name = "Chat", MonitorId = "DISPLAY1_2560x1440", Rect = new ZoneRect(0.58, 0, 0.42, 0.5) },
        new() { Id = "web", Number = 3, Name = "Browser", MonitorId = "DISPLAY1_2560x1440", Rect = new ZoneRect(0.58, 0.5, 0.42, 0.5) },
        new() { Id = "unity", Number = 4, Name = "Unity", MonitorId = "DISPLAY2_1920x1080", Rect = new ZoneRect(0, 0, 1, 0.62) },
        new() { Id = "music", Number = 5, Name = "Music", MonitorId = "DISPLAY2_1920x1080", Rect = new ZoneRect(0, 0.62, 0.5, 0.38) },
        new() { Id = "spare", Number = 6, MonitorId = "DISPLAY2_1920x1080", Rect = new ZoneRect(0.5, 0.62, 0.5, 0.38) },
        new() { Id = "notes", Number = 7, Name = "Notes", MonitorId = "DISPLAY3_1920x1200", Rect = new ZoneRect(0, 0, 0.5, 1) },
    ];

    private static Dictionary<string, List<ZoneWindowSnapshot>> Windows() => new()
    {
        ["code"] =
        [
            new(0x1001, "WindowZonesView.axaml — Helm — Visual Studio Code", "Code", @"C:\Program Files\Microsoft VS Code\Code.exe", false, true),
            new(0x1002, "BubblePop — Rider", "rider64", @"C:\Program Files\JetBrains\Rider\bin\rider64.exe", false, false),
        ],
        ["chat"] =
        [
            new(0x1003, "Slack | #release-bubble-pop | Bubble Studio", "slack", "", false, true),
            new(0x1004, "Zalo", "Zalo", "", false, false),
        ],
        ["web"] = [new(0x1005, "BP-1287 Booster not consumed after a loss — Jira — Google Chrome", "chrome", "", false, true)],
        ["unity"] = [new(0x1006, "BubblePop - Main.unity - Android - Unity 2022.3.45f1", "Unity", "", true, true)],
        ["music"] = [new(0x1007, "Spotify Premium", "Spotify", "", false, true)],
    };

    private sealed class Host : IWindowZonesPageHost
    {
        public void Opened() { }

        public IReadOnlyList<MonitorInfo> Monitors() => WindowZonesDemo.Monitors;

        public IImage? IconFor(string processPath) => null;
    }
}

/// <summary>
/// Window Zones' engine, pretended: the zones from the file, pretend windows in them. Open, close, show and take out
/// change only this list. On/off follows the Gallery's switch (<see cref="FakeModule"/>).
/// </summary>
internal sealed class PretendZones : IWindowZones
{
    private readonly FakeModule _module;
    private readonly Dictionary<string, List<ZoneWindowSnapshot>> _windows;
    private IReadOnlyList<ZoneSnapshot> _zones = [];
    private string? _status;

    public PretendZones(FakeModule module, ISettingsStoreFactory settings, Dictionary<string, List<ZoneWindowSnapshot>> windows)
    {
        _module = module;
        _windows = windows;
        Settings = settings.Get<WindowZonesSettings>(WindowZonesIds.ModuleId);
        Data = new WindowZonesData(settings);
        Data.Changed += (_, _) => Update();
        module.PropertyChanged += (_, e) =>
        {
            PropertyChanged?.Invoke(this, e);
            if (e.PropertyName == nameof(IModule.IsEnabled)) Update();
        };
        Update();
    }

    public string Id => WindowZonesIds.ModuleId;
    public string DisplayName => "Window Zones";
    public string Description => "";
    public ModuleGroup Group => ModuleGroup.WindowsAndDesktop;

    public bool IsEnabled
    {
        get => _module.IsEnabled;
        set => _module.IsEnabled = value;
    }

    public string? StatusMessage
    {
        get => _status;
        set
        {
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage)));
        }
    }

    public ISettingsStore<WindowZonesSettings> Settings { get; }

    public WindowZonesData Data { get; }

    public IReadOnlyList<ZoneSnapshot> Zones => _zones;

    public event EventHandler? ZonesChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What the pages asked for, in order (the UI tests read it).</summary>
    public List<string> Asked { get; } = [];

    public Task EnableAsync(CancellationToken ct) => Task.CompletedTask;

    public Task DisableAsync() => Task.CompletedTask;

    public void OpenEditor() => Asked.Add("editor");

    public void OpenZone(string zoneId) => SetMinimized(zoneId, false);

    public void CloseZone(string zoneId) => SetMinimized(zoneId, true);

    public void ShowZone(string zoneId) => Asked.Add("where " + zoneId);

    public void ShowWindow(nint handle) => Asked.Add($"show {handle}");

    public void Release(nint handle)
    {
        foreach (var list in _windows.Values) list.RemoveAll(w => w.Handle == handle);
        Update();
    }

    private void SetMinimized(string zoneId, bool minimized)
    {
        Asked.Add((minimized ? "close " : "open ") + zoneId);
        if (_windows.TryGetValue(zoneId, out var list))
            for (var i = 0; i < list.Count; i++) list[i] = list[i] with { IsMinimized = minimized };
        Update();
    }

    private void Update()
    {
        var monitors = WindowZonesDemo.Monitors;
        _zones = Data.GetZones()
            .OrderBy(z => z.Number ?? int.MaxValue)
            .Select(z =>
            {
                var monitor = monitors.FirstOrDefault(m => string.Equals(m.Id, z.MonitorId, StringComparison.OrdinalIgnoreCase));
                IReadOnlyList<ZoneWindowSnapshot> windows = IsEnabled && monitor is not null && _windows.TryGetValue(z.Id, out var list) ? [.. list] : [];
                return new ZoneSnapshot(z.Id, z.Number, z.Name, z.Label, monitor?.DisplayName ?? "Monitor not connected", monitor is not null, windows);
            })
            .ToList();
        ZonesChanged?.Invoke(this, EventArgs.Empty);
    }
}
