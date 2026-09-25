using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Modules.Zones.Editor;
using Helm.Modules.Zones.Engine;
using Helm.Modules.Zones.Layouts;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.Zones;

public sealed class ZonesModule : HelmModuleBase
{
    public const string ModuleId = "zones";
    private const string EditorHotkeyId = "editor";
    private const HotkeyModifiers LayoutModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Win | HotkeyModifiers.Alt;

    private readonly IHotkeyManager _hotkeys;
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly LowLevelMouseHook _mouseHook;
    private readonly LowLevelKeyboardHook _keyboardHook;
    private readonly ZonesEditorController _editor;
    private readonly ILogger<ZonesModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<HotkeyRegistration> _registrations = [];
    private ZonesEngine? _engine;
    private (HotkeyGesture Editor, bool Layouts) _registeredFor;

    public ZonesModule(
        ISettingsStoreFactory settings,
        ZonesDataService data,
        IHotkeyManager hotkeys,
        IWindowService windows,
        IMonitorService monitors,
        LowLevelMouseHook mouseHook,
        LowLevelKeyboardHook keyboardHook,
        ZonesEditorController editor,
        ILogger<ZonesModule> logger)
    {
        Settings = settings.Get<ZonesSettings>(ModuleId);
        Data = data;
        _hotkeys = hotkeys;
        _windows = windows;
        _monitors = monitors;
        _mouseHook = mouseHook;
        _keyboardHook = keyboardHook;
        _editor = editor;
        _logger = logger;
        Settings.Changed += (_, _) => _ = ApplySettingsAsync();
    }

    public override string Id => ModuleId;
    public override string DisplayName => "Zones";
    public override string Description => "Draw your own zones — on one monitor or across several — and snap windows into them. Hold Shift while dragging a window to see the zones.";
    public override ModuleGroup Group => ModuleGroup.WindowingAndLayouts;
    public override SymbolRegular Icon => SymbolRegular.SlideLayout24;
    public override Type SettingsPageType => typeof(ZonesPage);

    public ISettingsStore<ZonesSettings> Settings { get; }

    public ZonesDataService Data { get; }

    public override IReadOnlyList<HotkeyDefinition> Hotkeys
    {
        get
        {
            var s = Settings.Current;
            var activation = s.ActivationKey switch
            {
                ZoneActivationKey.Ctrl => HotkeyModifiers.Ctrl,
                ZoneActivationKey.Alt => HotkeyModifiers.Alt,
                _ => HotkeyModifiers.Shift,
            };
            var list = new List<HotkeyDefinition>
            {
                new(ModuleId, EditorHotkeyId, "Open the layout editor", s.EditorHotkey),
            };
            if (!s.AlwaysShowZones)
                list.Add(new HotkeyDefinition(ModuleId, "activate", "Activate zones while dragging (hold)", new HotkeyGesture(activation, 0)));
            if (s.LayoutHotkeys)
                list.Add(new HotkeyDefinition(ModuleId, "layout-1", "Switch to layout 1 (…9)", new HotkeyGesture(LayoutModifiers, '1')));
            if (s.CycleWindowsInZone)
            {
                list.Add(new HotkeyDefinition(ModuleId, "cycle-prev", "Previous window in the zone", new HotkeyGesture(HotkeyModifiers.Win, VirtualKeyNames.PageUp)));
                list.Add(new HotkeyDefinition(ModuleId, "cycle-next", "Next window in the zone", new HotkeyGesture(HotkeyModifiers.Win, VirtualKeyNames.PageDown)));
            }
            if (s.OverrideWindowsSnap)
            {
                list.Add(new HotkeyDefinition(ModuleId, "prev", "Move window to the previous zone", new HotkeyGesture(HotkeyModifiers.Win, VirtualKeyNames.Left)));
                list.Add(new HotkeyDefinition(ModuleId, "next", "Move window to the next zone", new HotkeyGesture(HotkeyModifiers.Win, VirtualKeyNames.Right)));
            }
            return list;
        }
    }

    public void OpenEditor() => _editor.Show();

    public override async Task EnableAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is not null) return;
            StatusMessage = null;
            try
            {
                _engine = await ZonesEngine.StartAsync(_windows, _monitors, Data, _mouseHook, _keyboardHook, _logger,
                    ZonesEngine.Options.From(Settings.Current)).ConfigureAwait(false);
            }
            catch (HookInstallException ex)
            {
                _logger.LogError(ex, "Zones could not install its hooks");
                StatusMessage = $"Window snapping is unavailable: {ex.Message}";
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
            _editor.Close();
            if (_engine is { } engine)
            {
                _engine = null;
                await engine.DisposeAsync().ConfigureAwait(false);
            }
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
    }

    /// <summary>Editor shortcut plus Ctrl+Win+Alt+1…9 (one registration each); failures become one InfoBar message.</summary>
    private async Task RegisterHotkeysAsync()
    {
        UnregisterHotkeys();
        var s = Settings.Current;
        var problems = new List<string>();

        var editor = await _hotkeys.TryRegisterAsync(Hotkeys[0], () => _editor.Toggle()).ConfigureAwait(false);
        _registrations.Add(editor);
        if (!editor.IsRegistered) problems.Add($"The editor shortcut {s.EditorHotkey} is not active: {editor.Error}");

        if (s.LayoutHotkeys && _engine is { } engine)
        {
            var failed = new List<int>();
            for (var n = 1; n <= ZoneLayout.MaxNumber; n++)
            {
                var number = n;
                var definition = new HotkeyDefinition(ModuleId, $"layout-{n}", $"Switch to layout {n}", new HotkeyGesture(LayoutModifiers, '0' + n));
                var registration = await _hotkeys.TryRegisterAsync(definition, () => engine.SwitchLayout(number)).ConfigureAwait(false);
                _registrations.Add(registration);
                if (!registration.IsRegistered) failed.Add(n);
            }
            if (failed.Count > 0)
                problems.Add($"Ctrl+Win+Alt+{string.Join(", ", failed)} could not be registered (used by another application).");
        }

        _registeredFor = (s.EditorHotkey, s.LayoutHotkeys);
        StatusMessage = problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
        NotifyHotkeysChanged();
    }

    private async Task ApplySettingsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            NotifyHotkeysChanged();
            if (_engine is null) return;
            _engine.UpdateOptions(ZonesEngine.Options.From(Settings.Current));
            if (_registeredFor != (Settings.Current.EditorHotkey, Settings.Current.LayoutHotkeys))
                await RegisterHotkeysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply Zones settings");
        }
        finally
        {
            _gate.Release();
        }
    }
}
