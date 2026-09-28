using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault.Crypto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.Vault.Session;

public enum VaultState
{
    /// <summary>No vault on this device or (after a sync) on the account.</summary>
    NotSetUp,
    Locked,
    Unlocked,
}

/// <summary>
/// Someone proved on some device that they saved the Emergency Kit of this recovery key (synced, so the other devices of
/// the account do not ask again). Holds no secret: the recovery id is printed on the kit.
/// </summary>
public sealed record VaultKitConfirmation(string VaultId, string RecoveryId, long ConfirmedAtMs)
{
    public static string RecordId(string vaultId, string recoveryId) => $"{vaultId}|{recoveryId}";
}

/// <summary>Too many wrong passwords in a row: try again after <see cref="RetryAfter"/>.</summary>
public sealed class VaultThrottledException(TimeSpan retryAfter)
    : Exception($"Too many wrong passwords. Try again in {Math.Ceiling(retryAfter.TotalSeconds)} seconds.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Whether the vault is locked, and the only holder of the unlocked <see cref="VaultKey"/>. Locking zeroes the key.
/// Property changes are raised on whatever thread caused them (the auto-lock timer uses the thread pool).
/// </summary>
public sealed partial class VaultSession : ObservableObject, IDisposable
{
    public const string KeyringCollection = "vault.keyring";

    /// <summary>Emergency Kit confirmations (<see cref="VaultKitConfirmation"/>), one record per vault and recovery key.</summary>
    public const string KitCollection = "vault.kits";
    public const int MaxDeviceUnlockFailures = 5;
    private const int FreePasswordAttempts = 3;

    private readonly ISyncedCollection<VaultKeyringData> _keyrings;
    private readonly ISyncedCollection<VaultKitConfirmation>? _kits;
    private readonly ISettingsStore<VaultSettings> _settings;
    private readonly ISettingsStore<VaultDeviceState> _device;
    private readonly IVaultDeviceUnlock _deviceUnlock;
    private readonly ISyncService _sync;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly ITimer _autoLock;
    private VaultKey? _key;

    [ObservableProperty]
    private VaultState _state;

    [ObservableProperty]
    private VaultKeyringData? _keyring;

    /// <summary>Unlocked with the recovery key: a new password must be set before anything else.</summary>
    [ObservableProperty]
    private bool _mustChangePassword;

    public VaultSession(
        ISyncedCollection<VaultKeyringData> keyrings,
        ISettingsStoreFactory settings,
        IVaultDeviceUnlock deviceUnlock,
        ISyncService sync,
        TimeProvider? time = null,
        ILogger<VaultSession>? logger = null,
        ISyncedCollection<VaultKitConfirmation>? kits = null)
    {
        _keyrings = keyrings;
        _kits = kits;
        if (kits is not null) kits.Changed += (_, _) => OnPropertyChanged(nameof(RecoveryKitConfirmed));
        _settings = settings.Get<VaultSettings>(VaultSettings.StoreId);
        _device = settings.Get<VaultDeviceState>(VaultDeviceState.StoreId);
        _deviceUnlock = deviceUnlock;
        _sync = sync;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _autoLock = _time.CreateTimer(_ => Lock("inactivity"), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _keyrings.Changed += (_, _) => RefreshKeyring();
        RefreshKeyring();
    }

    /// <summary>Raised just before the key is zeroed, so holders of decrypted data can drop it.</summary>
    public event EventHandler? Locking;

    public string? VaultId => Keyring?.VaultId;

    /// <summary>Tests use cheap Argon2 parameters; everything else keeps the default.</summary>
    internal Func<VaultKdf> NewKdf { get; set; } = VaultKdf.Default;

    public string DeviceUnlockName => _deviceUnlock.Name;

    /// <summary>
    /// Other vaults found on the account: two devices created a vault before they had synced. Shown so the user can
    /// merge them (docs/vault-design.md); never deleted automatically.
    /// </summary>
    public IReadOnlyList<VaultKeyringData> OtherVaults { get; private set; } = [];

    /// <summary>
    /// The current recovery key's Emergency Kit was confirmed, on this device or (synced) on another one of the account.
    /// A new recovery key has a new id, so it asks again.
    /// </summary>
    public bool RecoveryKitConfirmed => Keyring is { } k
        && (_device.Current.ConfirmedRecoveryId == k.RecoveryId || _kits?.Get(VaultKitConfirmation.RecordId(k.VaultId, k.RecoveryId)) is not null);

    public bool IsDeviceUnlockEnrolled => VaultId is { } id && _deviceUnlock.IsEnrolled(id);

    /// <summary>Quick unlock is enrolled and allowed now (the password was typed recently enough, few failures).</summary>
    public bool CanUseDeviceUnlock =>
        State == VaultState.Locked && IsDeviceUnlockEnrolled && _device.Current.FailedDeviceUnlocks < MaxDeviceUnlockFailures
        && PasswordRequiredAt > _time.GetUtcNow();

    /// <summary>When quick unlock stops working and the password is required again.</summary>
    public DateTimeOffset PasswordRequiredAt =>
        DateTimeOffset.FromUnixTimeMilliseconds(_device.Current.LastPasswordUnlockMs) + TimeSpan.FromDays(Math.Max(0, _settings.Current.RequirePasswordDays));

    /// <summary>The unlocked key. Only the vault's own services use it; they must not keep it past <see cref="Locking"/>.</summary>
    /// <exception cref="VaultLockedException">The vault is locked.</exception>
    internal VaultKey Key
    {
        get
        {
            lock (_gate) return _key ?? throw new VaultLockedException();
        }
    }

    /// <summary>
    /// Creates the vault. When sync is set up it first syncs, so a vault made on another device is found instead of a
    /// second one being created. Returns the recovery key, to be shown once and saved in the Emergency Kit.
    /// </summary>
    /// <exception cref="VaultKeyException">Weak password, or a vault already exists.</exception>
    /// <exception cref="InvalidOperationException">Sync is set up but the server cannot be reached.</exception>
    public async Task<string> CreateAsync(string password, CancellationToken ct = default)
    {
        await SyncBeforeCreatingAsync(ct).ConfigureAwait(true);
        if (Keyring is not null) throw new VaultKeyException("This account already has a vault. Unlock it with its password.");

        var created = await Task.Run(() => VaultKeyring.Create(password, Now(), NewKdf()), ct).ConfigureAwait(true);
        _keyrings.Upsert(VaultKeyringData.RecordId(created.Keyring.VaultId), created.Keyring);
        _logger.LogInformation("Vault {VaultId} created", created.Keyring.VaultId);
        RefreshKeyring();
        _device.Update(d => d.ConfirmedRecoveryId = null);
        OnPasswordUnlocked();
        SetUnlocked(created.Key, mustChangePassword: false);
        return created.RecoveryKey;
    }

    /// <exception cref="VaultKeyException">Wrong password.</exception>
    /// <exception cref="VaultThrottledException">Too many wrong passwords in a row.</exception>
    public async Task UnlockAsync(string password, CancellationToken ct = default)
    {
        var keyring = RequireKeyring();
        ThrowIfThrottled();
        VaultKey key;
        try
        {
            key = await Task.Run(() => VaultKeyring.UnlockWithPassword(keyring, password), ct).ConfigureAwait(true);
        }
        catch (VaultKeyException)
        {
            _device.Update(d =>
            {
                d.FailedPasswordAttempts++;
                d.LastFailedAttemptMs = Now();
            });
            _logger.LogWarning("Wrong vault password ({Count} in a row)", _device.Current.FailedPasswordAttempts);
            throw;
        }
        OnPasswordUnlocked();
        SetUnlocked(key, mustChangePassword: false);
    }

    /// <summary>Opens the vault with the Emergency Kit; a new password must then be set (<see cref="MustChangePassword"/>).</summary>
    public async Task UnlockWithRecoveryKeyAsync(string recoveryKey, CancellationToken ct = default)
    {
        var keyring = RequireKeyring();
        ThrowIfThrottled();
        VaultKey key;
        try
        {
            key = await Task.Run(() => VaultKeyring.UnlockWithRecoveryKey(keyring, recoveryKey), ct).ConfigureAwait(true);
        }
        catch (VaultKeyException)
        {
            _device.Update(d =>
            {
                d.FailedPasswordAttempts++;
                d.LastFailedAttemptMs = Now();
            });
            throw;
        }
        _logger.LogWarning("Vault unlocked with the recovery key");
        MarkKitConfirmed(keyring);
        SetUnlocked(key, mustChangePassword: true);
    }

    /// <returns>False when quick unlock is not allowed now or the user cancelled.</returns>
    public async Task<bool> UnlockWithDeviceAsync(CancellationToken ct = default)
    {
        if (!CanUseDeviceUnlock || VaultId is not { } vaultId) return false;
        byte[]? raw;
        try
        {
            raw = await _deviceUnlock.UnlockAsync(vaultId, ct).ConfigureAwait(true);
        }
        catch (VaultDeviceUnlockException ex)
        {
            _logger.LogWarning(ex, "Quick unlock key is no longer valid; removing it");
            _deviceUnlock.Remove();
            OnPropertyChanged(nameof(IsDeviceUnlockEnrolled));
            OnPropertyChanged(nameof(CanUseDeviceUnlock));
            throw;
        }
        if (raw is null)
        {
            _device.Update(d => d.FailedDeviceUnlocks++);
            OnPropertyChanged(nameof(CanUseDeviceUnlock));
            return false;
        }
        try
        {
            var key = new VaultKey(raw);
            // A stale enrollment (another vault, a damaged store) is refused rather than unlocking into garbage.
            if (!RequireKeyring().Matches(key))
            {
                key.Dispose();
                _deviceUnlock.Remove();
                OnPropertyChanged(nameof(IsDeviceUnlockEnrolled));
                throw new VaultDeviceUnlockException("The quick unlock key does not belong to this vault. Unlock with the password and turn quick unlock on again.");
            }
            SetUnlocked(key, mustChangePassword: false);
            _device.Update(d => d.FailedDeviceUnlocks = 0);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>Turns on quick unlock for this device (the vault must be unlocked).</summary>
    public async Task<bool> EnableDeviceUnlockAsync(CancellationToken ct = default)
    {
        var vaultId = RequireKeyring().VaultId;
        var key = Key;
        var copy = key.Bytes.ToArray();
        try
        {
            var enrolled = await _deviceUnlock.EnrollAsync(vaultId, copy, ct).ConfigureAwait(true);
            if (enrolled) _device.Update(d => d.FailedDeviceUnlocks = 0);
            OnPropertyChanged(nameof(IsDeviceUnlockEnrolled));
            return enrolled;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public void DisableDeviceUnlock()
    {
        _deviceUnlock.Remove();
        OnPropertyChanged(nameof(IsDeviceUnlockEnrolled));
        OnPropertyChanged(nameof(CanUseDeviceUnlock));
    }

    /// <param name="currentPassword">Required, except right after unlocking with the recovery key.</param>
    public async Task ChangePasswordAsync(string? currentPassword, string newPassword, CancellationToken ct = default)
    {
        var keyring = RequireKeyring();
        var key = Key;
        if (!MustChangePassword)
        {
            ArgumentNullException.ThrowIfNull(currentPassword);
            ThrowIfThrottled();
            try
            {
                using var check = await Task.Run(() => VaultKeyring.UnlockWithPassword(keyring, currentPassword), ct).ConfigureAwait(true);
            }
            catch (VaultKeyException)
            {
                _device.Update(d =>
                {
                    d.FailedPasswordAttempts++;
                    d.LastFailedAttemptMs = Now();
                });
                throw;
            }
        }
        var changed = await Task.Run(() => VaultKeyring.ChangePassword(keyring, key, newPassword, Now()), ct).ConfigureAwait(true);
        _keyrings.Upsert(VaultKeyringData.RecordId(changed.VaultId), changed);
        _logger.LogInformation("Vault password changed");
        MustChangePassword = false;
        OnPasswordUnlocked();
        RefreshKeyring();
    }

    /// <summary>Replaces the recovery key: the old Emergency Kit stops working. Returns the new key, to show once.</summary>
    public async Task<string> NewRecoveryKeyAsync(string password, CancellationToken ct = default)
    {
        var keyring = RequireKeyring();
        var key = Key;
        ThrowIfThrottled();
        using (await Task.Run(() => VaultKeyring.UnlockWithPassword(keyring, password), ct).ConfigureAwait(true)) { }
        var (changed, text) = await Task.Run(() => VaultKeyring.NewRecoveryKey(keyring, key, Now()), ct).ConfigureAwait(true);
        _keyrings.Upsert(VaultKeyringData.RecordId(changed.VaultId), changed);
        _device.Update(d => d.ConfirmedRecoveryId = null);
        _logger.LogInformation("Vault recovery key replaced");
        RefreshKeyring();
        return text;
    }

    /// <summary>The user proved they saved the Emergency Kit (typed back part of the recovery key).</summary>
    /// <returns>False when <paramref name="recoveryKey"/> is not the current recovery key.</returns>
    public bool ConfirmRecoveryKit(string recoveryKey)
    {
        var keyring = RequireKeyring();
        var secret = VaultRecoveryKey.Parse(recoveryKey);
        if (secret is null) return false;
        try
        {
            if (VaultRecoveryKey.IdOf(secret) != keyring.RecoveryId) return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
        MarkKitConfirmed(keyring);
        return true;
    }

    /// <summary>
    /// Makes a vault restored from a backup this device's vault: its keyring is stored (and synced) as it was, and the
    /// key that opened the backup unlocks it. Only when the account has no vault yet.
    /// </summary>
    internal void AdoptRestoredVault(VaultKeyringData keyring, VaultKey key, bool mustChangePassword = false)
    {
        if (Keyring is not null) throw new VaultKeyException("This account already has a vault. Restore the missing items into it instead.");
        if (!keyring.Matches(key)) throw new VaultKeyException("The key does not belong to this backup's vault.");
        _keyrings.Upsert(VaultKeyringData.RecordId(keyring.VaultId), keyring);
        RefreshKeyring();
        _device.Update(d => d.ConfirmedRecoveryId = null);
        if (!mustChangePassword) OnPasswordUnlocked();
        SetUnlocked(key, mustChangePassword);
        _logger.LogWarning("Vault {VaultId} restored from a backup", keyring.VaultId);
    }

    /// <summary>
    /// Syncs first when sync is set up, so a restore or a new vault never races a vault that exists on the server.
    /// </summary>
    internal async Task SyncBeforeCreatingAsync(CancellationToken ct)
    {
        if (_sync.Status.State == SyncState.NotConfigured) return;
        var synced = await _sync.SyncNowAsync(ct).ConfigureAwait(true);
        if (synced.Outcome is not (SyncRunOutcome.Completed or SyncRunOutcome.NotConfigured))
            throw new InvalidOperationException("Connect to the sync server first, so Helm can check whether this account already has a vault.");
        RefreshKeyring();
    }

    /// <summary>Locks now. Safe to call from any thread and when already locked.</summary>
    public void Lock(string reason)
    {
        VaultKey? key;
        lock (_gate)
        {
            key = _key;
            _key = null;
        }
        if (key is null) return;
        _autoLock.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try { Locking?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogError(ex, "A vault lock handler failed"); }
        key.Dispose();
        MustChangePassword = false;
        _logger.LogInformation("Vault locked ({Reason})", reason);
        State = Keyring is null ? VaultState.NotSetUp : VaultState.Locked;
        OnPropertyChanged(nameof(CanUseDeviceUnlock));
    }

    /// <summary>The user did something in the vault: restart the inactivity timer.</summary>
    public void Touch()
    {
        if (State != VaultState.Unlocked) return;
        var minutes = _settings.Current.AutoLockMinutes;
        _autoLock.Change(minutes > 0 ? TimeSpan.FromMinutes(minutes) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        Lock("shutdown");
        _autoLock.Dispose();
    }

    private void SetUnlocked(VaultKey key, bool mustChangePassword)
    {
        VaultKey? previous;
        lock (_gate)
        {
            previous = _key;
            _key = key;
        }
        previous?.Dispose();
        MustChangePassword = mustChangePassword;
        if (Keyring is { } keyring && _device.Current.ConfirmedRecoveryId == keyring.RecoveryId) ShareKitConfirmation(keyring);
        State = VaultState.Unlocked;
        OnPropertyChanged(nameof(RecoveryKitConfirmed));
        _logger.LogInformation("Vault unlocked");
        Touch();
    }

    /// <summary>Remembers the confirmation on this device and shares it with the account's other devices.</summary>
    private void MarkKitConfirmed(VaultKeyringData keyring)
    {
        _device.Update(d => d.ConfirmedRecoveryId = keyring.RecoveryId);
        ShareKitConfirmation(keyring);
        OnPropertyChanged(nameof(RecoveryKitConfirmed));
    }

    /// <summary>Also for confirmations made before they were synced (0.10.1 and older kept them on the device only).</summary>
    private void ShareKitConfirmation(VaultKeyringData keyring)
    {
        if (_kits is null) return;
        var id = VaultKitConfirmation.RecordId(keyring.VaultId, keyring.RecoveryId);
        if (_kits.Get(id) is not null) return;
        try
        {
            _kits.Upsert(id, new VaultKitConfirmation(keyring.VaultId, keyring.RecoveryId, Now()));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not share the Emergency Kit confirmation");
        }
    }

    private void OnPasswordUnlocked() => _device.Update(d =>
    {
        d.LastPasswordUnlockMs = Now();
        d.FailedPasswordAttempts = 0;
        d.FailedDeviceUnlocks = 0;
    });

    private void ThrowIfThrottled()
    {
        var failures = _device.Current.FailedPasswordAttempts;
        if (failures < FreePasswordAttempts) return;
        // 2 s, 4 s, 8 s … up to 5 minutes after the third wrong password in a row.
        var wait = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, failures - FreePasswordAttempts + 1)));
        var until = DateTimeOffset.FromUnixTimeMilliseconds(_device.Current.LastFailedAttemptMs) + wait;
        var left = until - _time.GetUtcNow();
        if (left > TimeSpan.Zero) throw new VaultThrottledException(left);
    }

    private VaultKeyringData RequireKeyring() => Keyring ?? throw new InvalidOperationException("Create the vault first.");

    /// <summary>The oldest keyring is the vault; any other one is a second vault created before the devices synced.</summary>
    private void RefreshKeyring()
    {
        var all = _keyrings.All().Select(k => k.Value).Where(k => k.V >= 1).OrderBy(k => k.CreatedAtMs).ThenBy(k => k.VaultId, StringComparer.Ordinal).ToList();
        var primary = all.FirstOrDefault();
        OtherVaults = all.Skip(1).ToList();
        Keyring = primary;
        if (State != VaultState.Unlocked) State = primary is null ? VaultState.NotSetUp : VaultState.Locked;
        OnPropertyChanged(nameof(OtherVaults));
        OnPropertyChanged(nameof(RecoveryKitConfirmed));
        OnPropertyChanged(nameof(CanUseDeviceUnlock));
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();
}

public sealed class VaultLockedException() : InvalidOperationException("The vault is locked.");
