using Helm.Core.Capture;
using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// Quick Capture on Windows: the shortcut (Win+Alt+N by default) opens a small box over any app; Enter saves the text
/// into Notes or Tracker through their <see cref="ICaptureTarget"/>s and the box closes. Only the tools that are on
/// are offered. No settings page content beyond the shortcut and the default target.
/// </summary>
public sealed class QuickCaptureModule : HelmModuleBase, IDisposable
{
    public const string ModuleId = "quick-capture";
    private const string HotkeyId = "open";

    private readonly IHotkeyManager _hotkeys;
    private readonly IUiDispatcher _ui;
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly IEnumerable<ICaptureTarget> _targets;
    private readonly IServiceProvider _services;
    private readonly ILogger<QuickCaptureModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HotkeyRegistration? _registration;
    private FloatingPanelWindow? _window;
    private CapturePanel? _panel;

    // IModuleHost is resolved when the box opens: it depends on the module list, which contains this module.
    public QuickCaptureModule(ISettingsStoreFactory settings, IHotkeyManager hotkeys, IUiDispatcher ui, IWindowService windows,
        IMonitorService monitors, IEnumerable<ICaptureTarget> targets, IServiceProvider services, ILogger<QuickCaptureModule> logger)
    {
        Settings = settings.Get<QuickCaptureSettings>(ModuleId);
        _hotkeys = hotkeys;
        _ui = ui;
        _windows = windows;
        _monitors = monitors;
        _targets = targets;
        _services = services;
        _logger = logger;
        Settings.Changed += (_, _) => _ = ApplySettingsAsync();
    }

    public override string Id => ModuleId;
    public override string DisplayName => "Quick Capture";
    public override string Description => "Press a shortcut in any app to save a note, a task or a debt in a second, without opening Helm.";
    public override ModuleGroup Group => ModuleGroup.Advanced;
    public override SymbolRegular Icon => SymbolRegular.Flash24;
    public override System.Windows.Media.ImageSource IconImage => QuickCaptureLogo.Image;
    public override Type SettingsPageType => typeof(QuickCapturePage);

    public override IReadOnlyList<HotkeyDefinition> Hotkeys =>
        [new HotkeyDefinition(ModuleId, HotkeyId, "Open Quick Capture", Settings.Current.Hotkey)];

    public ISettingsStore<QuickCaptureSettings> Settings { get; }

    /// <summary>Every target any tool offers, whether the tool is on or not (for the settings page).</summary>
    public IReadOnlyList<ICaptureTarget> AllTargets => _targets.OrderBy(t => t.Order).ToList();

    public override async Task EnableAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StatusMessage = null;
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
            _ui.Post(() => _window?.Dismiss());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens the box (the shortcut). Any thread.</summary>
    public void Open() => _ui.Post(() => Show(null, null));

    /// <summary>Opens the box with a text and a target already chosen (from the command palette). Any thread.</summary>
    public void Open(string text, string? targetId) => _ui.Post(() => Show(text, targetId));

    /// <summary>The targets of the tools that are on now.</summary>
    public IReadOnlyList<ICaptureTarget> AvailableTargets()
    {
        var host = _services.GetRequiredService<IModuleHost>();
        return CaptureRouter.Available(_targets, id => host.Find(id)?.IsEnabled == true);
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _gate.Dispose();
    }

    private void Show(string? text, string? targetId)
    {
        try
        {
            if (!IsEnabled) return;
            if (_window is null)
            {
                _panel = new CapturePanel(new CaptureBoxViewModel(AvailableTargets, Settings));
                _window = new FloatingPanelWindow(_windows, _monitors, 640) { Panel = _panel, Title = "Quick Capture" };
                _panel.Finished += (_, _) => _window.Dismiss();
            }
            _panel!.Prepare(text, targetId);
            _window.Summon();
        }
        catch (Exception ex)
        {
            // Called from the shortcut: never let an exception escape.
            _logger.LogError(ex, "Could not open Quick Capture");
            StatusMessage = $"Could not open the capture box: {ex.Message}";
        }
    }

    private async Task RegisterHotkeyAsync()
    {
        _registration?.Dispose();
        _registration = null;
        var definition = Hotkeys[0];
        _registration = await _hotkeys.TryRegisterAsync(definition, Open).ConfigureAwait(false);
        StatusMessage = _registration.IsRegistered ? null : $"The shortcut {definition.Gesture} is not active: {_registration.Error}";
        NotifyHotkeysChanged();
    }

    private async Task ApplySettingsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsEnabled || _registration is null)
            {
                NotifyHotkeysChanged();
                return;
            }
            if (_registration.Definition.Gesture != Settings.Current.Hotkey) await RegisterHotkeyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply Quick Capture settings");
        }
        finally
        {
            _gate.Release();
        }
    }
}
