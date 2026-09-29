using Helm.Core.Desktop;
using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Palette;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Helm.Modules.CommandPalette;

/// <summary>
/// The command palette on Windows: the shortcut (Alt+Space by default) opens one search box over any app. It finds
/// Helm's pages and settings, the tools' own data (notes, tasks, vault items while the vault is open) through their
/// <see cref="IPaletteProvider"/>s, and the Start menu's apps. Enter opens the result.
/// </summary>
public sealed class CommandPaletteModule : HelmModuleBase, IDisposable
{
    public const string ModuleId = "command-palette";
    private const string HotkeyId = "open";

    private readonly IHotkeyManager _hotkeys;
    private readonly IUiDispatcher _ui;
    private readonly IWindowService _windows;
    private readonly IMonitorService _monitors;
    private readonly IServiceProvider _services;
    private readonly ILogger<CommandPaletteModule> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HotkeyRegistration? _registration;
    private FloatingPanelWindow? _window;
    private PalettePanel? _panel;
    private IReadOnlyList<IPaletteProvider>? _providers;
    private IReadOnlyList<ISlowPaletteProvider>? _slowProviders;

    // Providers and the module host are resolved when the palette opens: the shell's providers depend on the module
    // list, which contains this module.
    public CommandPaletteModule(ISettingsStoreFactory settings, IHotkeyManager hotkeys, IUiDispatcher ui, IWindowService windows,
        IMonitorService monitors, IServiceProvider services, ILogger<CommandPaletteModule> logger)
    {
        Settings = settings.Get<CommandPaletteSettings>(ModuleId);
        _hotkeys = hotkeys;
        _ui = ui;
        _windows = windows;
        _monitors = monitors;
        _services = services;
        _logger = logger;
        Settings.Changed += (_, _) => _ = ApplySettingsAsync();
    }

    public override string Id => ModuleId;
    public override string DisplayName => "Command Palette";
    public override string Description => "One shortcut, one box: your notes and tasks first, then apps, files, open windows and Windows settings, from any app.";
    public override ModuleGroup Group => ModuleGroup.Advanced;
    public override SymbolRegular Icon => SymbolRegular.Search24;
    public override System.Windows.Media.ImageSource IconImage => CommandPaletteLogo.Image;
    public override Type SettingsPageType => typeof(CommandPalettePage);

    public override IReadOnlyList<HotkeyDefinition> Hotkeys =>
        [new HotkeyDefinition(ModuleId, HotkeyId, "Open the command palette", Settings.Current.Hotkey)];

    public ISettingsStore<CommandPaletteSettings> Settings { get; }

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

    /// <summary>Opens the palette. Any thread.</summary>
    public void Open() => _ui.Post(Show);

    public void Dispose()
    {
        _registration?.Dispose();
        _gate.Dispose();
    }

    private void Show()
    {
        try
        {
            if (!IsEnabled) return;
            _providers ??= _services.GetServices<IPaletteProvider>().ToList();
            _slowProviders ??= _services.GetServices<ISlowPaletteProvider>().ToList();
            foreach (var apps in _providers.OfType<AppsPaletteProvider>()) apps.RefreshIfStale();
            if (_window is null)
            {
                var viewModel = new PaletteViewModel(Search, SearchSlowAsync);
                viewModel.Chosen += (_, item) => Run(item);
                _panel = new PalettePanel(viewModel);
                _window = new FloatingPanelWindow(_windows, _monitors, 680) { Panel = _panel, Title = "Command Palette" };
            }
            _panel!.Prepare();
            _window.Summon();
        }
        catch (Exception ex)
        {
            // Called from the shortcut: never let an exception escape.
            _logger.LogError(ex, "Could not open the command palette");
            StatusMessage = $"Could not open the command palette: {ex.Message}";
        }
    }

    private IReadOnlyList<PaletteItem> Search(PaletteQuery query)
    {
        var host = _services.GetRequiredService<IModuleHost>();
        return PaletteSearch.Search(_providers ?? [], id => host.Find(id)?.IsEnabled == true, query, _logger);
    }

    private Task<IReadOnlyList<PaletteItem>> SearchSlowAsync(PaletteQuery query, CancellationToken ct)
    {
        var host = _services.GetRequiredService<IModuleHost>();
        return PaletteSearch.SearchSlowAsync(_slowProviders ?? [], id => host.Find(id)?.IsEnabled == true, query, ct, _logger);
    }

    /// <summary>The palette closes first, so the result can bring its own window to the front.</summary>
    private void Run(PaletteItem item)
    {
        _window?.Dismiss();
        _ui.Post(() =>
        {
            try
            {
                item.Run();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Palette result {Title} failed", item.Title);
            }
        });
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
            _logger.LogError(ex, "Failed to apply command palette settings");
        }
        finally
        {
            _gate.Release();
        }
    }
}
