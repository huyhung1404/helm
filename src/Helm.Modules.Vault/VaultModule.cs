using Helm.Core.Hotkeys;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace Helm.Modules.Vault;

/// <summary>
/// Vault on Windows. While enabled it locks the vault when Windows locks or sleeps, empties old trash and runs the
/// daily backup when the vault is unlocked. Disabling it locks the vault and closes its window.
/// </summary>
public sealed class VaultModule(
    VaultSession session,
    VaultStore store,
    VaultBackupService backup,
    ISettingsStoreFactory settings,
    IUiDispatcher ui,
    ILogger<VaultModule> logger) : HelmModuleBase, IModuleContent
{
    public const string ModuleId = "vault";
    private readonly ISettingsStore<VaultSettings> _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
    private Timer? _maintenance;

    public override string Id => ModuleId;
    public override string DisplayName => "Vault";
    public override string Description => "Keeps passwords, secure notes, cards and documents encrypted, synced and backed up.";
    public override ModuleGroup Group => ModuleGroup.Advanced;
    public override SymbolRegular Icon => SymbolRegular.ShieldKeyhole24;
    public override System.Windows.Media.ImageSource IconImage => VaultIcon.Image;
    public override Type SettingsPageType => typeof(VaultPage);
    public Type ContentPageType => typeof(VaultContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys => [];

    public override Task EnableAsync(CancellationToken ct)
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _maintenance ??= new Timer(_ => ui.Post(RunMaintenance), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        return Task.CompletedTask;
    }

    public override Task DisableAsync()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _maintenance?.Dispose();
        _maintenance = null;
        session.Lock("Vault turned off");
        return Task.CompletedTask;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (!_settings.Current.LockOnSystemLock) return;
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            session.Lock($"Windows {e.Reason}");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (_settings.Current.LockOnSystemLock && e.Mode == PowerModes.Suspend) session.Lock("sleep");
    }

    private async void RunMaintenance()
    {
        // async void (timer): everything is caught, a failure here must never take Helm down.
        try
        {
            store.PurgeExpired(TimeSpan.FromDays(Math.Max(1, _settings.Current.TrashDays)));
            await backup.BackUpIfDueAsync().ConfigureAwait(true);
            StatusMessage = backup.IsOverdue && session.State != VaultState.NotSetUp
                ? backup.LastGoodBackup is null ? "The vault has no backup yet. Choose a backup folder below." : "The vault has not been backed up for over a week."
                : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Vault maintenance failed");
        }
    }
}
