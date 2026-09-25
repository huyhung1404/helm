using System.ComponentModel;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Modules;

public interface IModuleHost
{
    IReadOnlyList<IHelmModule> Modules { get; }

    IHelmModule? Find(string id);

    /// <summary>Enables every module whose saved state is enabled, then starts tracking toggles.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Disables every running module (exit, reset, update).</summary>
    Task StopAllAsync();
}

public sealed class ModuleRegistry : IModuleHost
{
    private readonly ISettingsStore<GeneralSettings> _general;
    private readonly ILogger<ModuleRegistry> _logger;
    private readonly Dictionary<IHelmModule, ModuleState> _states = new();
    private bool _started;

    public ModuleRegistry(IEnumerable<IHelmModule> modules, ISettingsStoreFactory settings, ILogger<ModuleRegistry> logger)
    {
        _logger = logger;
        _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
        Modules = modules.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var module in Modules) _states[module] = new ModuleState();
    }

    public IReadOnlyList<IHelmModule> Modules { get; }

    public IHelmModule? Find(string id) => Modules.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started) return;
        _started = true;

        foreach (var module in Modules)
        {
            var enabled = _general.Current.EnabledModules.TryGetValue(module.Id, out var saved) ? saved : true;
            module.IsEnabled = enabled;
            module.PropertyChanged += OnModulePropertyChanged;
        }

        foreach (var module in Modules.Where(m => m.IsEnabled))
            await SyncAsync(module, ct).ConfigureAwait(true);
    }

    public async Task StopAllAsync()
    {
        foreach (var module in Modules)
        {
            var state = _states[module];
            await state.Gate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (!state.Running) continue;
                await module.DisableAsync().ConfigureAwait(true);
                state.Running = false;
                _logger.LogInformation("Module {Id} disabled", module.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Module {Id} failed to disable", module.Id);
            }
            finally
            {
                state.Gate.Release();
            }
        }
    }

    private async void OnModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IHelmModule.IsEnabled) || sender is not IHelmModule module) return;
        try
        {
            _general.Update(s => s.EnabledModules[module.Id] = module.IsEnabled);
            await SyncAsync(module, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // async void: an escaping exception would terminate Helm.
            _logger.LogError(ex, "Toggling module {Id} failed", module.Id);
        }
    }

    /// <summary>Brings the running state in line with <see cref="IHelmModule.IsEnabled"/>, serialized per module.</summary>
    private async Task SyncAsync(IHelmModule module, CancellationToken ct)
    {
        var state = _states[module];
        await state.Gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (module.IsEnabled && !state.Running)
            {
                try
                {
                    await module.EnableAsync(ct).ConfigureAwait(true);
                    state.Running = true;
                    _logger.LogInformation("Module {Id} enabled", module.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Module {Id} failed to enable", module.Id);
                    if (module is HelmModuleBase b) b.StatusMessage = $"Could not start {module.DisplayName}: {ex.Message}";
                    try { await module.DisableAsync().ConfigureAwait(true); }
                    catch (Exception cleanupEx) { _logger.LogError(cleanupEx, "Module {Id} cleanup failed", module.Id); }
                }
            }
            else if (!module.IsEnabled && state.Running)
            {
                try
                {
                    await module.DisableAsync().ConfigureAwait(true);
                    _logger.LogInformation("Module {Id} disabled", module.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Module {Id} failed to disable", module.Id);
                }
                state.Running = false;
                if (module is HelmModuleBase b) b.StatusMessage = null;
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private sealed class ModuleState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Running { get; set; }
    }
}
