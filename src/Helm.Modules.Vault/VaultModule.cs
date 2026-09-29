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
    IHotkeyManager hotkeys,
    Platform.AutoTypeService autoType,
    ILogger<VaultModule> logger) : HelmModuleBase, IModuleContent
{
    public const string ModuleId = "vault";
    private const string AutoTypeHotkeyId = "auto-type";
    private readonly ISettingsStore<VaultSettings> _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
    private readonly SemaphoreSlim _hotkeyGate = new(1, 1);
    private Timer? _maintenance;
    private HotkeyRegistration? _autoType;

    public override string Id => ModuleId;
    public override string DisplayName => "Vault";
    public override string Description => "Keeps passwords, secure notes, cards and documents encrypted, synced and backed up.";
    public override ModuleGroup Group => ModuleGroup.Advanced;
    public override SymbolRegular Icon => SymbolRegular.ShieldKeyhole24;
    public override System.Windows.Media.ImageSource IconImage => VaultIcon.Image;
    public override Type SettingsPageType => typeof(VaultPage);
    public Type ContentPageType => typeof(VaultContentPage);
    public override IReadOnlyList<HotkeyDefinition> Hotkeys =>
        autoType.Settings.Current.AutoTypeHotkey.IsEmpty ? []
            : [new HotkeyDefinition(ModuleId, AutoTypeHotkeyId, "Auto-type a login into the window in front", autoType.Settings.Current.AutoTypeHotkey)];

    public override async Task EnableAsync(CancellationToken ct)
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _maintenance ??= new Timer(_ => ui.Post(RunMaintenance), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        autoType.Settings.Changed += OnAutoTypeSettingsChanged;
        await ApplyAutoTypeHotkeyAsync().ConfigureAwait(false);
    }

    public override async Task DisableAsync()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _maintenance?.Dispose();
        _maintenance = null;
        autoType.Settings.Changed -= OnAutoTypeSettingsChanged;
        await _hotkeyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _autoType?.Dispose();
            _autoType = null;
        }
        finally
        {
            _hotkeyGate.Release();
        }
        session.Lock("Vault turned off");
    }

    /// <summary>Why the auto-type shortcut is not active (taken by another app), or null.</summary>
    public string? AutoTypeError { get; private set; }

    private void OnAutoTypeSettingsChanged(object? sender, Platform.VaultWindowsSettings e) => _ = ApplyAutoTypeHotkeyAsync();

    private async Task ApplyAutoTypeHotkeyAsync()
    {
        await _hotkeyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var definition = Hotkeys.FirstOrDefault();
            if (_autoType is { IsRegistered: true } current && definition is not null && current.Definition.Gesture == definition.Gesture) return;
            _autoType?.Dispose();
            _autoType = null;
            AutoTypeError = null;
            if (definition is not null && IsEnabled)
            {
                _autoType = await hotkeys.TryRegisterAsync(definition, autoType.Trigger).ConfigureAwait(false);
                AutoTypeError = _autoType.IsRegistered ? null : $"The auto-type shortcut {definition.Gesture} is not active: {_autoType.Error}";
            }
            // Only a problem is shown; the backup reminder (set by maintenance) stays otherwise.
            if (AutoTypeError is not null) StatusMessage = AutoTypeError;
            NotifyHotkeysChanged();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Registering the auto-type shortcut failed");
        }
        finally
        {
            _hotkeyGate.Release();
        }
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
