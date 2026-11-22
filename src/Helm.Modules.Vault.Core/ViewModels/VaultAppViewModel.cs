using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Sync;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.Vault.ViewModels;

public enum VaultScreen
{
    /// <summary>No vault yet: create one, or restore one from a backup.</summary>
    Setup,
    Unlock,
    Items,
}

/// <summary>
/// The vault window (PC) or page (Android): which screen shows, the setup, unlock and Emergency Kit flows, the item
/// list, and the warnings that must not be missed (held deletions, no recent backup, unconfirmed kit). Passwords are
/// set by the view (password boxes are not bindable) and cleared after use.
/// </summary>
public sealed partial class VaultAppViewModel : ObservableObject
{
    private readonly VaultSession _session;
    private readonly VaultStore _store;
    private readonly VaultBackupService _backup;
    private readonly SyncEngine _engine;
    private readonly IVaultPlatform _platform;
    private readonly IUiDispatcher _ui;
    // Kept so the timer is not collected; the view model lives as long as the app.
    private readonly Timer _totpTimer;
    private readonly ILogger _logger;

    [ObservableProperty] private VaultScreen _screen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    // Setup
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";

    // Unlock
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _useRecoveryKey;
    [ObservableProperty] private string _recoveryKey = "";

    // Emergency Kit (shown once after the vault or a new recovery key is created)
    [ObservableProperty] private EmergencyKit? _kit;
    [ObservableProperty] private string _kitConfirmation = "";
    [ObservableProperty] private string? _kitError;

    // The user guide, shown in place of the vault screens (it works locked, unlocked and before setup)
    [ObservableProperty] private bool _isGuideOpen;

    public IReadOnlyList<Guide.GuideBlock> Guide => Vault.Guide.VaultGuide.Blocks;

    public VaultAppViewModel(VaultSession session, VaultStore store, VaultFiles files, VaultBackupService backup, SyncEngine engine,
        IVaultPlatform platform, IUiDispatcher ui, ILogger<VaultAppViewModel>? logger = null)
    {
        _session = session;
        _store = store;
        _backup = backup;
        _engine = engine;
        _platform = platform;
        _ui = ui;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Items = new ItemsViewModel(store, files, session, platform);
        // One-time codes change every 30 s: the list and the open item show the current one with a countdown.
        // The codes count down once a second, but only while the items show: locked (or never opened) the timer
        // sleeps, instead of waking the phone every second for as long as Helm runs.
        _totpTimer = new Timer(_ => _ui.Post(() =>
        {
            if (!IsItems) return;
            Items.TickTotp(DateTimeOffset.UtcNow);
            OnPropertyChanged(nameof(LockCountdown));
        }), null, Timeout.Infinite, Timeout.Infinite);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsItems)) UpdateTotpTimer();
        };
        UpdateTotpTimer();

        session.PropertyChanged += OnSessionChanged;
        session.Locking += (_, _) => _ui.Post(Items.Clear);
        store.Changed += (_, _) => _ui.Post(() =>
        {
            if (Screen == VaultScreen.Items) Items.Refresh();
        });
        engine.StatusChanged += (_, _) => _ui.Post(RaiseWarnings);
        backup.Completed += (_, _) => _ui.Post(RaiseWarnings);
        UpdateScreen();
    }

    public ItemsViewModel Items { get; }

    public bool IsSetup => Screen == VaultScreen.Setup;

    public bool IsUnlock => Screen == VaultScreen.Unlock;

    public bool IsItems => Screen == VaultScreen.Items && !MustChangePassword;

    public bool HasKit => Kit is not null;

    public VaultSession Session => _session;

    public string DeviceUnlockName => _session.DeviceUnlockName;

    public bool CanUseDeviceUnlock => _session.CanUseDeviceUnlock;

    public bool MustChangePassword => _session.MustChangePassword;

    /// <summary>The vault password would not be accepted as a new one today: whoever copies a backup could guess it offline.</summary>
    public string? WeakPasswordWarning => _session.PasswordIsWeak && !MustChangePassword
        ? "Your vault password is too easy to guess by today's rules. Anyone who copies your backup folder or this device's data can try passwords offline, with no limit, so the password is the only lock there. Change it in the Vault settings."
        : null;

    /// <summary>
    /// Forgets every password and recovery key typed into the vault screens but not used yet, so nothing typed is still
    /// there (or shown, if it was revealed) when someone comes back to the page. The view calls it when the page is left
    /// or hidden; locking does it too.
    /// </summary>
    public void ForgetTypedSecrets()
    {
        Password = RecoveryKey = NewPassword = ConfirmPassword = KitConfirmation = "";
        UseRecoveryKey = false;
        Error = null;
    }

    public string PasswordStrength => SyncKeyVault.EstimateStrength(NewPassword) switch
    {
        PassphraseStrength.TooShort => $"At least {VaultKeyring.MinPasswordLength} characters",
        PassphraseStrength.Weak => "Too easy to guess",
        PassphraseStrength.Fair => "Fair",
        _ => "Strong",
    };

    /// <summary>Another device deleted many items; sync of the vault waits for a decision.</summary>
    public string? HeldWarning => _engine.Holds.FirstOrDefault(h => h.Collection == VaultStore.Collection) is { } hold
        ? $"Another device deleted {hold.Records.Count} of your {hold.LiveRecords} vault items without using the trash. Nothing was deleted here yet."
        : null;

    /// <summary>Only when no device of the account backed the vault up for a week (one device with a folder is enough).</summary>
    public string? BackupWarning => !_backup.IsOverdue ? null
        : _backup.LastGoodBackupAnywhere is not { } last ? "This vault has never been backed up. Choose a backup folder in the Vault settings (on one of your devices)."
        : $"No device has backed up this vault for {(DateTimeOffset.UtcNow - last).Days} days." + (_backup.LastError is { } e ? " " + e : "");

    /// <summary>"locks in 4:52" while the vault is open and locks when idle; null otherwise.</summary>
    public string? LockCountdown
    {
        get
        {
            if (!IsItems || _session.AutoLockAt is not { } at) return null;
            var left = at - _session.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"locks in {(int)left.TotalMinutes}:{left.Seconds:00}";
        }
    }

    /// <summary>The backup line under the sections: "Backed up", "Backup failed" or "Not backed up".</summary>
    public string BackupTitle => _backup.LastError is not null && _backup.Location is not null ? "Backup failed"
        : _backup.LastGoodBackupAnywhere is null ? "Not backed up" : "Backed up";

    /// <summary>"3 days ago · OneDrive · verified", or what to do about it.</summary>
    public string BackupDetail
    {
        get
        {
            if (_backup.LastError is { } error && _backup.Location is not null) return error;
            if (_backup.LastGoodBackupAnywhere is not { } last)
                return "Choose a backup folder in the Vault settings (on one of your devices).";
            var place = Backup.VaultBackupService.PlaceName(_backup.Location) ?? "on another device";
            return $"{Ago(last)} · {place} · verified";
        }
    }

    /// <summary>The latest backup is recent and this device's last attempt did not fail.</summary>
    public bool BackupOk => BackupTitle == "Backed up" && !_backup.IsOverdue;

    /// <summary>"just now", "5 minutes ago", "yesterday", "3 days ago".</summary>
    internal string Ago(DateTimeOffset when)
    {
        var span = _session.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return Plural((int)span.TotalMinutes, "minute") + " ago";
        if (span < TimeSpan.FromDays(1)) return Plural((int)span.TotalHours, "hour") + " ago";
        if (span < TimeSpan.FromDays(2)) return "yesterday";
        return Plural((int)span.TotalDays, "day") + " ago";

        static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
    }

    partial void OnNewPasswordChanged(string value) => OnPropertyChanged(nameof(PasswordStrength));

    private void UpdateTotpTimer()
    {
        try
        {
            if (IsItems) _totpTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            else _totpTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    partial void OnScreenChanged(VaultScreen value)
    {
        OnPropertyChanged(nameof(IsSetup));
        OnPropertyChanged(nameof(IsUnlock));
        OnPropertyChanged(nameof(IsItems));
    }

    partial void OnKitChanged(EmergencyKit? value)
    {
        OnPropertyChanged(nameof(HasKit));
    }

    [RelayCommand]
    private Task CreateAsync() => RunAsync(async () =>
    {
        if (NewPassword != ConfirmPassword) throw new VaultKeyException("The two passwords differ.");
        var recovery = await _session.CreateAsync(NewPassword).ConfigureAwait(true);
        NewPassword = ConfirmPassword = "";
        ShowKit(recovery);
    });

    [RelayCommand]
    private Task RestoreFromBackupAsync() => RunAsync(async () =>
    {
        var location = await _platform.PickBackupLocationAsync(CancellationToken.None).ConfigureAwait(true);
        if (location is null) return;
        var target = _backup.OpenLocation(location) ?? throw new InvalidOperationException("That folder cannot be opened.");
        var repository = (await VaultBackupRepository.FindAsync(target).ConfigureAwait(true)).FirstOrDefault()
            ?? throw new InvalidOperationException("There is no Helm Vault backup in that folder. Pick the folder that contains \"HelmVault-…\".");
        if (repository.Snapshots.Count == 0) throw new InvalidOperationException("That backup has no snapshot.");
        using var key = UseRecoveryKey
            ? await Task.Run(() => VaultKeyring.UnlockWithRecoveryKey(repository.Keyring, RecoveryKey)).ConfigureAwait(true)
            : await Task.Run(() => VaultKeyring.UnlockWithPassword(repository.Keyring, Password)).ConfigureAwait(true);
        var count = await _backup.RestoreVaultAsync(repository, repository.Snapshots[0], key, mustChangePassword: UseRecoveryKey).ConfigureAwait(true);
        _logger.LogWarning("Vault restored from {Snapshot} ({Count} records)", repository.Snapshots[0], count);
        Password = RecoveryKey = "";
        UseRecoveryKey = false;
    });

    [RelayCommand]
    private Task UnlockAsync() => RunAsync(async () =>
    {
        if (UseRecoveryKey) await _session.UnlockWithRecoveryKeyAsync(RecoveryKey).ConfigureAwait(true);
        else await _session.UnlockAsync(Password).ConfigureAwait(true);
        Password = RecoveryKey = "";
        UseRecoveryKey = false;
    });

    [RelayCommand]
    private Task QuickUnlockAsync() => RunAsync(async () =>
    {
        if (!await _session.UnlockWithDeviceAsync().ConfigureAwait(true) && _session.State != VaultState.Unlocked)
            Error = CanUseDeviceUnlock ? null : "Type the vault password: quick unlock is not available right now.";
    });

    /// <summary>After unlocking with the recovery key: the new password.</summary>
    [RelayCommand]
    private Task SetNewPasswordAsync() => RunAsync(async () =>
    {
        if (NewPassword != ConfirmPassword) throw new VaultKeyException("The two passwords differ.");
        await _session.ChangePasswordAsync(null, NewPassword).ConfigureAwait(true);
        NewPassword = ConfirmPassword = "";
    });

    [RelayCommand]
    private void Lock() => _session.Lock("user");

    /// <summary>"Forgot it? Use the recovery key" (and back): what was typed in the other box is dropped.</summary>
    [RelayCommand]
    private void ToggleRecoveryKey()
    {
        UseRecoveryKey = !UseRecoveryKey;
        Password = RecoveryKey = "";
        Error = null;
    }

    [RelayCommand]
    private void ShowGuide() => IsGuideOpen = true;

    [RelayCommand]
    private void CloseGuide() => IsGuideOpen = false;

    [RelayCommand]
    private Task SaveKitAsync() => Kit is null ? Task.CompletedTask : RunAsync(() => _platform.SaveEmergencyKitAsync(Kit, CancellationToken.None));

    [RelayCommand]
    private void ConfirmKit()
    {
        KitError = null;
        if (!_session.ConfirmRecoveryKit(KitConfirmation))
        {
            KitError = "That is not the recovery key shown above. Type it exactly as you saved it.";
            return;
        }
        Kit = null;
        KitConfirmation = "";
        RaiseWarnings();
    }

    [RelayCommand]
    private Task ApplyHeldAsync() => RunAsync(async () =>
    {
        if (!await _platform.ConfirmAsync("Apply the deletions", "Delete these items on this device too? They stay in your backups.", "Delete").ConfigureAwait(true)) return;
        await _engine.ApproveHeldAsync(VaultStore.Collection).ConfigureAwait(true);
    });

    [RelayCommand]
    private Task KeepHeldAsync() => RunAsync(() => _engine.RejectHeldAsync(VaultStore.Collection));

    /// <summary>Shows the Emergency Kit for a recovery key just created (by setup or the settings page).</summary>
    public void ShowKit(string recoveryKey)
    {
        var keyring = _session.Keyring!;
        Kit = new EmergencyKit(keyring.VaultId, keyring.RecoveryId, recoveryKey, DateTimeOffset.Now);
        KitConfirmation = "";
        KitError = null;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VaultSession.State) or nameof(VaultSession.Keyring) or nameof(VaultSession.MustChangePassword) or nameof(VaultSession.CanUseDeviceUnlock))
            _ui.Post(UpdateScreen);
        else if (e.PropertyName == nameof(VaultSession.PasswordIsWeak))
            _ui.Post(RaiseWarnings);
    }

    private void UpdateScreen()
    {
        var screen = _session.State switch
        {
            VaultState.NotSetUp => VaultScreen.Setup,
            VaultState.Locked => VaultScreen.Unlock,
            _ => VaultScreen.Items,
        };
        var changed = screen != Screen;
        Screen = screen;
        if (screen == VaultScreen.Items && changed)
        {
            Items.Refresh();
            // The first unlock of the day is a good moment for the backup.
            _ = _backup.BackUpIfDueAsync();
        }
        if (screen != VaultScreen.Items)
        {
            Items.Clear();
            if (changed)
            {
                Kit = null;
                // Locked again (or the vault went away): nothing typed earlier waits in the boxes.
                ForgetTypedSecrets();
            }
        }
        OnPropertyChanged(nameof(CanUseDeviceUnlock));
        OnPropertyChanged(nameof(MustChangePassword));
        OnPropertyChanged(nameof(IsItems));
        RaiseWarnings();
    }

    private void RaiseWarnings()
    {
        OnPropertyChanged(nameof(HeldWarning));
        OnPropertyChanged(nameof(BackupWarning));
        OnPropertyChanged(nameof(WeakPasswordWarning));
        OnPropertyChanged(nameof(BackupTitle));
        OnPropertyChanged(nameof(BackupDetail));
        OnPropertyChanged(nameof(BackupOk));
        OnPropertyChanged(nameof(LockCountdown));
    }

    private async Task RunAsync(Func<Task> work)
    {
        IsBusy = true;
        Error = null;
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is VaultKeyException or VaultThrottledException or VaultDeviceUnlockException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException or BlobUnavailableException or HttpRequestException)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vault action failed");
            Error = "Something went wrong: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
