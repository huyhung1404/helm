using Helm.Core.Desktop;
using Helm.Core.Hooks;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.AlwaysOnTop;

public sealed class AlwaysOnTopModule : HelmModuleBase
{
    public const string ModuleId = "always-on-top";
    private const string HotkeyId = "toggle";

    private readonly IHotkeyManager _hotkeys;
    private readonly IWindowService _windows;
    private readonly ILogger<AlwaysOnTopModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AlwaysOnTopEngine? _engine;
    private HotkeyRegistration? _registration;
    private IReadOnlyList<PinnedWindowInfo> _pinned = [];

    public AlwaysOnTopModule(ISettingsStoreFactory settings, IHotkeyManager hotkeys, IWindowService windows, ILogger<AlwaysOnTopModule> logger)
    {
        Settings = settings.Get<AlwaysOnTopSettings>(ModuleId);
        _hotkeys = hotkeys;
        _windows = windows;
        _logger = logger;
        Settings.Changed += (_, _) => _ = ApplySettingsAsync();
    }

    public override string Id => ModuleId;
    public override string DisplayName => "Always On Top";
    public override string Description => "Pin any window above all others with a shortcut. Pinned windows get a colored border so you always know which ones they are.";
    public override ModuleGroup Group => ModuleGroup.WindowingAndLayouts;
    public override SymbolRegular Icon => SymbolRegular.Pin24;
    public override Type SettingsPageType => typeof(AlwaysOnTopPage);

    public override IReadOnlyList<HotkeyDefinition> Hotkeys =>
        [new HotkeyDefinition(ModuleId, HotkeyId, "Pin or unpin the active window", Settings.Current.Hotkey)];

    public ISettingsStore<AlwaysOnTopSettings> Settings { get; }

    /// <summary>Snapshot of pinned windows (updated from the engine thread).</summary>
    public IReadOnlyList<PinnedWindowInfo> PinnedWindows => _pinned;

    /// <summary>Raised on a background thread whenever <see cref="PinnedWindows"/> changes.</summary>
    public event EventHandler? PinnedWindowsChanged;

    public override async Task EnableAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is not null) return;
            StatusMessage = null;
            try
            {
                _engine = await AlwaysOnTopEngine.StartAsync(_windows, _logger, AlwaysOnTopEngine.Options.From(Settings.Current)).ConfigureAwait(false);
            }
            catch (HookInstallException ex)
            {
                _logger.LogError(ex, "Always On Top could not hook window events");
                StatusMessage = $"Window tracking is unavailable: {ex.Message}";
                throw;
            }
            _engine.PinnedChanged += OnPinnedChanged;
            await RegisterHotkeyAsync().ConfigureAwait(false);
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
            _registration?.Dispose();
            _registration = null;
            if (_engine is { } engine)
            {
                _engine = null;
                engine.PinnedChanged -= OnPinnedChanged;
                await engine.DisposeAsync().ConfigureAwait(false); // un-topmosts every pinned window
            }
            OnPinnedChanged([]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Unpin(nint hwnd) => _engine?.Unpin(hwnd);

    public Task UnpinAllAsync() => _engine?.UnpinAllAsync() ?? Task.CompletedTask;

    private async Task RegisterHotkeyAsync()
    {
        _registration?.Dispose();
        _registration = null;
        var engine = _engine;
        if (engine is null) return;

        var definition = Hotkeys[0];
        _registration = await _hotkeys.TryRegisterAsync(definition, engine.ToggleForeground).ConfigureAwait(false);
        StatusMessage = _registration.IsRegistered ? null : $"The shortcut {definition.Gesture} is not active: {_registration.Error}";
        NotifyHotkeysChanged();
    }

    private async Task ApplySettingsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_engine is null)
            {
                NotifyHotkeysChanged();
                return;
            }
            _engine.UpdateOptions(AlwaysOnTopEngine.Options.From(Settings.Current));
            if (_registration?.Definition.Gesture != Settings.Current.Hotkey) await RegisterHotkeyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply Always On Top settings");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnPinnedChanged(IReadOnlyList<PinnedWindowInfo> pinned)
    {
        _pinned = pinned;
        PinnedWindowsChanged?.Invoke(this, EventArgs.Empty);
    }
}
