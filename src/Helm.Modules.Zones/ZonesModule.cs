using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Helm.Modules.Zones.Editor;
using Helm.Modules.Zones.Engine;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.Zones;

public sealed class ZonesModule : HelmModuleBase
{
    public const string ModuleId = "zones";
    private const string EditorHotkeyId = "editor";

    private readonly IHotkeyManager _hotkeys;
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly LowLevelMouseHook _mouseHook;
    private readonly LowLevelKeyboardHook _keyboardHook;
    private readonly ZonesEditorController _editor;
    private readonly ILogger<ZonesModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ZonesEngine? _engine;
    private HotkeyRegistration? _editorRegistration;

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
    public override string Description => "Divide each monitor into zones and snap windows into them. Hold Shift while dragging a window to see the zones.";
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
            if (s.OverrideWindowsSnap)
            {
                list.Add(new HotkeyDefinition(ModuleId, "prev", "Move window to the previous zone", new HotkeyGesture(HotkeyModifiers.Win, VirtualKeyNames.Left)));
                list.Add(new HotkeyDefinition(ModuleId, "next", "Move window to the next zone", new HotkeyGesture(HotkeyModifiers.Win, VirtualKeyNames.Right)));
            }
            return list;
        }
    }

    public void OpenEditor() => _editor.Show(Settings.Current.PickerColumns);

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
            await RegisterEditorHotkeyAsync().ConfigureAwait(false);
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
            _editorRegistration?.Dispose();
            _editorRegistration = null;
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

    private async Task RegisterEditorHotkeyAsync()
    {
        _editorRegistration?.Dispose();
        var definition = Hotkeys[0];
        _editorRegistration = await _hotkeys.TryRegisterAsync(definition, () => _editor.Toggle(Settings.Current.PickerColumns)).ConfigureAwait(false);
        StatusMessage = _editorRegistration.IsRegistered ? null : $"The editor shortcut {definition.Gesture} is not active: {_editorRegistration.Error}";
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
            if (_editorRegistration?.Definition.Gesture != Settings.Current.EditorHotkey) await RegisterEditorHotkeyAsync().ConfigureAwait(false);
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
