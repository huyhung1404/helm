using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.WindowZones.Editor;
using Helm.Modules.WindowZones.Engine;
using Helm.Modules.WindowZones.Model;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.WindowZones;

/// <summary>
/// Window Zones: draw zones on your screens and drop windows into them (hold Shift while dragging). Each zone is a
/// drawer of windows: Ctrl+Alt+its number opens it, brings it to the front or closes it, and Win+PgDn / Win+PgUp
/// switch between its windows like tabs.
/// </summary>
public sealed class WindowZonesModule : HelmModuleBase, IModuleContent
{
    public const string ModuleId = "window-zones";
    private const string EditorHotkeyId = "editor";

    private readonly IHotkeyManager _hotkeys;
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly IZoneTabStrip _tabStrip;
    private readonly LowLevelMouseHook _mouseHook;
    private readonly LowLevelKeyboardHook _keyboardHook;
    private readonly ZonesEditorController _editor;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<WindowZonesModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<HotkeyRegistration> _registrations = [];
    private WindowZonesEngine? _engine;
    private string _registeredFor = string.Empty;
    private IReadOnlyList<ZoneSnapshot> _zones = [];

    public WindowZonesModule(
        ISettingsStoreFactory settings,
        WindowZonesData data,
        IHotkeyManager hotkeys,
        IWindowService windows,
        IMonitorService monitors,
        ZoneTabStrip tabStrip,
        LowLevelMouseHook mouseHook,
        LowLevelKeyboardHook keyboardHook,
        ZonesEditorController editor,
        IUiDispatcher ui,
        ILogger<WindowZonesModule> logger)
    {
        Settings = settings.Get<WindowZonesSettings>(ModuleId);
        Data = data;
        _hotkeys = hotkeys;
        _windows = windows;
        _monitors = monitors;
        _tabStrip = tabStrip;
        _mouseHook = mouseHook;
        _keyboardHook = keyboardHook;
        _editor = editor;
        _ui = ui;
        _logger = logger;
        Settings.Changed += (_, _) => _ = ApplySettingsAsync();
        Data.Changed += (_, _) => _ = OnZonesEditedAsync();
        _zones = Offline();
    }

    public override string Id => ModuleId;
    public override string DisplayName => "Window Zones";
    public override string Description => "Draw zones on your screens and drop windows into them. Each zone keeps its windows: open or close it with a shortcut, and switch between them like tabs.";
    public override ModuleGroup Group => ModuleGroup.WindowsAndDesktop;
    public override SymbolRegular Icon => SymbolRegular.SlideLayout24;
    public override System.Windows.Media.ImageSource IconImage => WindowZonesLogo.Image;
    public override Type SettingsPageType => typeof(WindowZonesPage);
    public Type ContentPageType => typeof(WindowZonesContentPage);

    public ISettingsStore<WindowZonesSettings> Settings { get; }

    public WindowZonesData Data { get; }

    /// <summary>Every zone and the windows in it right now (no windows while the tool is off).</summary>
    public IReadOnlyList<ZoneSnapshot> Zones => _zones;

    /// <summary>Raised on a background thread whenever <see cref="Zones"/> changes.</summary>
    public event EventHandler? ZonesChanged;

    public override IReadOnlyList<HotkeyDefinition> Hotkeys
    {
        get
        {
            var s = Settings.Current;
            var list = new List<HotkeyDefinition>
            {
                new(ModuleId, "zone-1", "Open or close zone 1 (…9)", s.ZoneHotkey(1)),
                new(ModuleId, "next", "Next window in the zone", s.NextWindowHotkey),
                new(ModuleId, "previous", "Previous window in the zone", s.PreviousWindowHotkey),
                new(ModuleId, "drag", "Drop a window into a zone (hold while dragging)", new HotkeyGesture(s.DragModifier, 0)),
            };
            if (!s.EditorHotkey.IsEmpty) list.Add(new HotkeyDefinition(ModuleId, EditorHotkeyId, "Edit the zones", s.EditorHotkey));
            return list.Where(h => !h.Gesture.IsEmpty || h.Gesture.Modifiers != HotkeyModifiers.None).ToList();
        }
    }

    public void OpenEditor() => _editor.Show();

    public void OpenZone(string zoneId) => _engine?.OpenZone(zoneId);

    public void CloseZone(string zoneId) => _engine?.CloseZone(zoneId);

    public void ShowZone(string zoneId) => _engine?.ShowZone(zoneId);

    public void ShowWindow(nint handle) => _engine?.ShowWindow(handle);

    public void Release(nint handle) => _engine?.Release(handle);

    public override async Task EnableAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is not null) return;
            StatusMessage = null;
            try
            {
                _engine = await WindowZonesEngine.StartAsync(_windows, _monitors, Data, _tabStrip, _mouseHook, _keyboardHook, _logger,
                    WindowZonesEngine.Options.From(Settings.Current), OnZonesChanged).ConfigureAwait(false);
            }
            catch (HookInstallException ex)
            {
                _logger.LogError(ex, "Window Zones could not install its hooks");
                StatusMessage = $"Window Zones cannot watch windows and keys: {ex.Message}";
                throw;
            }
            await RegisterHotkeysAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task DisableAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            UnregisterHotkeys();
            await _ui.InvokeAsync(_editor.Close).ConfigureAwait(false);
            if (_engine is { } engine)
            {
                _engine = null;
                await engine.DisposeAsync().ConfigureAwait(false);
            }
            // Closed zones stay closed: their windows are minimized, on the taskbar, and come back next time.
            OnZonesChanged(Offline());
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UnregisterHotkeys()
    {
        foreach (var r in _registrations) r.Dispose();
        _registrations.Clear();
        _registeredFor = string.Empty;
    }

    /// <summary>The editor shortcut plus one shortcut per numbered zone; failures become one InfoBar message.</summary>
    private async Task RegisterHotkeysAsync()
    {
        UnregisterHotkeys();
        var engine = _engine;
        if (engine is null) return;
        var s = Settings.Current;
        var problems = new List<string>();

        if (!s.EditorHotkey.IsEmpty)
        {
            var editor = await _hotkeys.TryRegisterAsync(new HotkeyDefinition(ModuleId, EditorHotkeyId, "Edit the zones", s.EditorHotkey), _editor.Toggle).ConfigureAwait(false);
            _registrations.Add(editor);
            if (!editor.IsRegistered) problems.Add($"The editor shortcut {s.EditorHotkey} is not active: {editor.Error}");
        }

        var failed = new List<int>();
        foreach (var number in NumbersInUse())
        {
            var definition = new HotkeyDefinition(ModuleId, $"zone-{number}", $"Open or close zone {number}", s.ZoneHotkey(number));
            var registration = await _hotkeys.TryRegisterAsync(definition, () => engine.ToggleZone(number)).ConfigureAwait(false);
            _registrations.Add(registration);
            if (!registration.IsRegistered) failed.Add(number);
        }
        if (failed.Count > 0)
            problems.Add($"{string.Join("+", new HotkeyGesture(s.ZoneModifiers, 0).ToKeycaps())}+{string.Join(", ", failed)} could not be registered (another app uses them). Pick other keys in the settings.");

        _registeredFor = RegistrationKey();
        StatusMessage = problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
        NotifyHotkeysChanged();
    }

    private IEnumerable<int> NumbersInUse() =>
        Data.GetZones().Select(z => z.Number).OfType<int>().Where(n => n is >= 1 and <= ZoneDefinition.MaxNumber).Distinct().Order();

    /// <summary>What the registered shortcuts depend on; re-register only when it changes.</summary>
    private string RegistrationKey() =>
        $"{Settings.Current.EditorHotkey}|{Settings.Current.ZoneModifiers}|{string.Join(",", NumbersInUse())}";

    private async Task ApplySettingsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            NotifyHotkeysChanged();
            if (_engine is null) return;
            _engine.UpdateOptions(WindowZonesEngine.Options.From(Settings.Current));
            if (_registeredFor != RegistrationKey()) await RegisterHotkeysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply Window Zones settings");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnZonesEditedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_engine is null)
            {
                OnZonesChanged(Offline());
                return;
            }
            // The engine refreshes itself (Data.Changed); only the zone shortcuts may need to follow.
            if (_registeredFor != RegistrationKey()) await RegisterHotkeysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply the edited zones");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The zones without their windows (the tool is off).</summary>
    private IReadOnlyList<ZoneSnapshot> Offline()
    {
        var monitors = _monitors.GetMonitors();
        return Data.GetZones()
            .OrderBy(z => z.Number ?? int.MaxValue)
            .Select(z =>
            {
                var monitor = monitors.FirstOrDefault(m => string.Equals(m.Id, z.MonitorId, StringComparison.OrdinalIgnoreCase));
                return new ZoneSnapshot(z.Id, z.Number, z.Name, z.Label, monitor?.DisplayName ?? "Monitor not connected", monitor is not null, []);
            })
            .ToList();
    }

    private void OnZonesChanged(IReadOnlyList<ZoneSnapshot> zones)
    {
        _zones = zones;
        ZonesChanged?.Invoke(this, EventArgs.Empty);
    }
}
