using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Core.Sync;

public enum SyncSetupStage
{
    /// <summary>No token on this device: everything stays local.</summary>
    NotConnected,
    /// <summary>Token saved, but no account key yet: create the account's passphrase or unlock with it.</summary>
    NeedsKey,
    Ready,
}

/// <summary>
/// Connecting a device (invite code or token), the account passphrase and recovery key, the account's devices,
/// key rotation, and signing out. The engine keeps syncing on its own once <see cref="Stage"/> is Ready.
/// </summary>
public sealed class SyncSetupService
{
    private const string BoundAccountKey = "bound_account_id";
    private readonly ISyncCredentialStore _credentials;
    private readonly IMasterKeyStore _keys;
    private readonly SyncApiClient _api;
    private readonly SyncEngine _engine;
    private readonly ILogger _logger;

    public SyncSetupService(ISyncCredentialStore credentials, IMasterKeyStore keys, SyncApiClient api, SyncEngine engine,
        ILogger<SyncSetupService>? logger = null)
    {
        _credentials = credentials;
        _keys = keys;
        _api = api;
        _engine = engine;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _engine.KeyEpochChanged += OnKeyEpochChanged;
    }

    public SyncSetupStage Stage =>
        _credentials.Load() is null ? SyncSetupStage.NotConnected
        : _keys.Load() is null ? SyncSetupStage.NeedsKey
        : SyncSetupStage.Ready;

    /// <summary>Raised after every change of <see cref="Stage"/>.</summary>
    public event EventHandler? StageChanged;

    /// <summary>True after another device rotated the account key and this one must unlock again.</summary>
    public bool KeyChangedElsewhere { get; private set; }

    public Uri Server => _credentials.Load()?.Server ?? SyncDefaults.Server;

    /// <summary>Connects with a token created for this device (on the admin page or by another device).</summary>
    public async Task<SyncAccountInfo> ConnectWithTokenAsync(string token, Uri? server = null, CancellationToken ct = default)
    {
        token = token.Trim();
        if (!SyncToken.TryParse(token, out _))
            throw new SyncRequestException(System.Net.HttpStatusCode.BadRequest, "invalid_token_format",
                "That is not a Helm sync token (it starts with helm_pat_). Check for a missing or extra character.");
        var credentials = new SyncCredentials(server ?? SyncDefaults.Server, token);
        var account = await _api.GetAccountAsync(credentials, ct).ConfigureAwait(false);
        Bind(account, credentials);
        return account;
    }

    /// <summary>Creates a new account from a one-time invite code and connects this device to it.</summary>
    public async Task<SyncAccountInfo> RedeemInviteAsync(string invite, string accountName, string deviceName, Uri? server = null,
        CancellationToken ct = default)
    {
        invite = invite.Trim();
        if (!SyncToken.IsInviteCode(invite))
            throw new SyncRequestException(System.Net.HttpStatusCode.BadRequest, "invalid_invite",
                "That is not a Helm invite code (it starts with helm_inv_). Check for a missing or extra character.");
        var credentials = await _api.RedeemInviteAsync(server ?? SyncDefaults.Server, invite, accountName, deviceName, ct).ConfigureAwait(false);
        var account = await _api.GetAccountAsync(credentials, ct).ConfigureAwait(false);
        Bind(account, credentials);
        return account;
    }

    /// <summary>True when another device already created the account passphrase (so this one should unlock).</summary>
    public async Task<bool> HasPassphraseAsync(CancellationToken ct = default) =>
        await _api.GetKeyringAsync(RequireCredentials(), ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// First device of the account: creates the master key, protects it with <paramref name="passphrase"/> and a new
    /// recovery key, and uploads the wrapped copy. Returns the recovery key, which must be shown once and kept safe.
    /// </summary>
    /// <exception cref="SyncKeyException">Weak passphrase, or another device set up the passphrase meanwhile.</exception>
    public async Task<string> CreatePassphraseAsync(string passphrase, CancellationToken ct = default)
    {
        var credentials = RequireCredentials();
        var created = await Task.Run(() => SyncKeyVault.Create(passphrase), ct).ConfigureAwait(false);
        using (created.Keys)
        {
            if (!await _api.PutKeyringAsync(credentials, 0, created.KeyringData, ct).ConfigureAwait(false))
                throw new SyncKeyException("Another device has just set this account's passphrase. Unlock with that passphrase instead.");
            SaveKeys(created.Keys);
        }
        await OnKeyReadyAsync().ConfigureAwait(false);
        return created.RecoveryKey;
    }

    /// <summary>Unlocks this device. A keyring still protected by PBKDF2 (Helm 0.5.0) is moved to Argon2id on the way.</summary>
    /// <exception cref="SyncKeyException">Wrong passphrase.</exception>
    public async Task UnlockWithPassphraseAsync(string passphrase, CancellationToken ct = default)
    {
        var credentials = RequireCredentials();
        var keyring = await RequireKeyringAsync(credentials, ct).ConfigureAwait(false);
        using var keys = await Task.Run(() => SyncKeyVault.UnlockWithPassphrase(keyring.Data, passphrase), ct).ConfigureAwait(false);
        SaveKeys(keys);
        await TryUpgradeAsync(credentials, keyring, passphrase, ct).ConfigureAwait(false);
        await OnKeyReadyAsync().ConfigureAwait(false);
    }

    /// <exception cref="SyncKeyException">Wrong recovery key.</exception>
    public async Task UnlockWithRecoveryKeyAsync(string recoveryKey, CancellationToken ct = default)
    {
        var keyring = await RequireKeyringAsync(RequireCredentials(), ct).ConfigureAwait(false);
        using var keys = await Task.Run(() => SyncKeyVault.UnlockWithRecoveryKey(keyring.Data, recoveryKey), ct).ConfigureAwait(false);
        SaveKeys(keys);
        await OnKeyReadyAsync().ConfigureAwait(false);
    }

    /// <summary>True while the account's passphrase is still protected by PBKDF2 rather than Argon2id.</summary>
    public async Task<bool> NeedsProtectionUpgradeAsync(CancellationToken ct = default)
    {
        var keyring = await _api.GetKeyringAsync(RequireCredentials(), ct).ConfigureAwait(false);
        return keyring is not null && SyncKeyVault.NeedsUpgrade(keyring.Data);
    }

    /// <summary>Moves the passphrase protection to Argon2id. Nothing else changes (data, devices, recovery key).</summary>
    /// <exception cref="SyncKeyException">Wrong passphrase.</exception>
    public async Task UpgradeProtectionAsync(string passphrase, CancellationToken ct = default)
    {
        var credentials = RequireCredentials();
        var keyring = await RequireKeyringAsync(credentials, ct).ConfigureAwait(false);
        if (!SyncKeyVault.NeedsUpgrade(keyring.Data)) return;
        var upgraded = await Task.Run(() => SyncKeyVault.Upgrade(keyring.Data, passphrase), ct).ConfigureAwait(false);
        if (!await _api.PutKeyringAsync(credentials, keyring.Version, upgraded, ct).ConfigureAwait(false))
            throw new SyncKeyException("Another device changed the keyring at the same time. Try again.");
    }

    /// <summary>Re-protects the account key with a new passphrase for every device that joins later.</summary>
    public async Task ChangePassphraseAsync(string newPassphrase, CancellationToken ct = default)
    {
        var credentials = RequireCredentials();
        using var keys = LoadKeys() ?? throw new InvalidOperationException("Unlock sync on this device first.");
        var current = await RequireKeyringAsync(credentials, ct).ConfigureAwait(false);
        var updated = await Task.Run(() => SyncKeyVault.ChangePassphrase(current.Data, keys, newPassphrase), ct).ConfigureAwait(false);
        if (!await _api.PutKeyringAsync(credentials, current.Version, updated, ct).ConfigureAwait(false))
            throw new SyncKeyException("Another device changed the passphrase at the same time. Try again.");
    }

    public Task<SyncAccountInfo> GetAccountAsync(CancellationToken ct = default) => _api.GetAccountAsync(RequireCredentials(), ct);

    public Task<IReadOnlyList<SyncDeviceToken>> ListDevicesAsync(CancellationToken ct = default) => _api.ListTokensAsync(RequireCredentials(), ct);

    /// <summary>A token for another device; the plaintext is returned only here.</summary>
    public Task<(SyncDeviceToken Device, string Token)> AddDeviceAsync(string name, int? expiresInDays = null, CancellationToken ct = default) =>
        _api.CreateTokenAsync(RequireCredentials(), name, expiresInDays, ct: ct);

    /// <summary>Stops a device from syncing. It keeps what it already downloaded; see <see cref="RemoveDeviceAndRotateKeyAsync"/>.</summary>
    public Task RevokeDeviceAsync(string tokenId, CancellationToken ct = default) => _api.RevokeTokenAsync(RequireCredentials(), tokenId, ct);

    /// <summary>
    /// Removes a device and rotates the account key, so the removed device (which still holds the old key) can
    /// never read anything written from now on. Every record is re-encrypted under the new key; other devices are
    /// asked for the passphrase once. Returns the NEW recovery key (the old one stops working).
    /// </summary>
    /// <exception cref="SyncKeyException">Wrong passphrase, or a concurrent change of the keyring.</exception>
    public async Task<string> RemoveDeviceAndRotateKeyAsync(string tokenId, string passphrase, CancellationToken ct = default)
    {
        var credentials = RequireCredentials();
        var keyring = await RequireKeyringAsync(credentials, ct).ConfigureAwait(false);
        // Check the passphrase before touching anything.
        using (await Task.Run(() => SyncKeyVault.UnlockWithPassphrase(keyring.Data, passphrase), ct).ConfigureAwait(false)) { }

        await _api.RevokeTokenAsync(credentials, tokenId, ct).ConfigureAwait(false);
        // Have everything the server has, so nothing is left sealed only under the old key.
        var synced = await _engine.SyncNowAsync(ct).ConfigureAwait(false);
        if (synced.Outcome != SyncRunOutcome.Completed)
            throw new SyncKeyException($"The device was removed, but the key could not be changed because sync did not complete ({synced.Outcome}). Try again.");

        keyring = await RequireKeyringAsync(credentials, ct).ConfigureAwait(false);
        var rotated = await Task.Run(() => SyncKeyVault.Rotate(keyring.Data, passphrase), ct).ConfigureAwait(false);
        using (rotated.Keys)
        {
            if (!await _api.PutKeyringAsync(credentials, keyring.Version, rotated.KeyringData, ct).ConfigureAwait(false))
                throw new SyncKeyException("Another device changed the keyring at the same time. Try again.");
            SaveKeys(rotated.Keys);
        }
        _engine.ReloadKey();
        var queued = _engine.Database.MarkAllForReseal();
        _logger.LogInformation("Account key rotated to epoch {Epoch}; re-encrypting {Count} records", SyncKeyVault.EpochOf(rotated.KeyringData), queued);
        await _engine.SyncNowAsync(ct).ConfigureAwait(false);
        return rotated.RecoveryKey;
    }

    /// <summary>
    /// Disconnects this device. Local data stays (it is the user's); the token and key are removed. With
    /// <paramref name="revokeToken"/> the token is also revoked on the server, when this device may do that.
    /// </summary>
    public async Task SignOutAsync(bool revokeToken, CancellationToken ct = default)
    {
        if (revokeToken && _credentials.Load() is { } credentials)
        {
            try
            {
                var account = await _api.GetAccountAsync(credentials, ct).ConfigureAwait(false);
                if (account.CanManageTokens) await _api.RevokeTokenAsync(credentials, account.TokenId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or SyncAuthException or SyncRequestException or TaskCanceledException)
            {
                _logger.LogInformation(ex, "Could not revoke this device's token while signing out; removing it locally anyway");
            }
        }
        _credentials.Clear();
        _keys.Clear();
        _engine.ReloadKey();
        KeyChangedElsewhere = false;
        StageChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task TryUpgradeAsync(SyncCredentials credentials, SyncKeyringDocument keyring, string passphrase, CancellationToken ct)
    {
        if (!SyncKeyVault.NeedsUpgrade(keyring.Data)) return;
        try
        {
            var upgraded = await Task.Run(() => SyncKeyVault.Upgrade(keyring.Data, passphrase), ct).ConfigureAwait(false);
            if (await _api.PutKeyringAsync(credentials, keyring.Version, upgraded, ct).ConfigureAwait(false))
                _logger.LogInformation("Sync passphrase protection upgraded to Argon2id");
        }
        catch (Exception ex) when (ex is HttpRequestException or SyncRequestException or SyncKeyException)
        {
            // Not fatal: the keyring still works; the Sync page offers the upgrade again.
            _logger.LogWarning(ex, "Could not upgrade the sync passphrase protection");
        }
    }

    private void OnKeyEpochChanged(object? sender, EventArgs e)
    {
        // Another device rotated the key: forget ours (it cannot read new data) and ask for the passphrase.
        _keys.Clear();
        _engine.ReloadKey();
        KeyChangedElsewhere = true;
        StageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Bind(SyncAccountInfo account, SyncCredentials credentials)
    {
        var db = _engine.Database;
        var bound = db.GetMetaValue(BoundAccountKey);
        if (bound is not null && bound != account.AccountId)
        {
            // This replica was synced with another account: upload its records afresh instead of mixing states.
            _logger.LogInformation("Sync account changed; local records will be uploaded to the new account");
            db.ResetSyncState();
            _keys.Clear();
        }
        db.SetMetaValue(BoundAccountKey, account.AccountId);
        _credentials.Save(credentials);
        _engine.ReloadKey();
        StageChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task OnKeyReadyAsync()
    {
        KeyChangedElsewhere = false;
        _engine.ReloadKey();
        StageChanged?.Invoke(this, EventArgs.Empty);
        await _engine.SyncNowAsync().ConfigureAwait(false);
    }

    private void SaveKeys(SyncKeySet keys)
    {
        var data = keys.Serialize();
        try
        {
            _keys.Save(data);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
        }
    }

    private SyncKeySet? LoadKeys() => SyncKeySet.TryParse(_keys.Load());

    private async Task<SyncKeyringDocument> RequireKeyringAsync(SyncCredentials credentials, CancellationToken ct) =>
        await _api.GetKeyringAsync(credentials, ct).ConfigureAwait(false)
        ?? throw new SyncKeyException("This account has no passphrase yet. Create one on this device.");

    private SyncCredentials RequireCredentials() =>
        _credentials.Load() ?? throw new InvalidOperationException("Connect this device to a sync account first.");
}
