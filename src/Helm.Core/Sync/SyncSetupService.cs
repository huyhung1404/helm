using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Core.Sync;

public enum SyncSetupStage
{
    /// <summary>No token on this device: everything stays local.</summary>
    NotConnected,
    /// <summary>Token saved, but no master key yet: create the account's passphrase or unlock with it.</summary>
    NeedsKey,
    Ready,
}

/// <summary>
/// Connecting a device (invite code or token), the account passphrase and recovery key, the account's devices, and
/// signing out. The engine keeps syncing on its own once <see cref="Stage"/> is <see cref="SyncSetupStage.Ready"/>.
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
    }

    public SyncSetupStage Stage =>
        _credentials.Load() is null ? SyncSetupStage.NotConnected
        : _keys.Load() is null ? SyncSetupStage.NeedsKey
        : SyncSetupStage.Ready;

    /// <summary>Raised after every change of <see cref="Stage"/> made through this service.</summary>
    public event EventHandler? StageChanged;

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
        var keys = await Task.Run(() => SyncKeyVault.Create(passphrase), ct).ConfigureAwait(false);
        try
        {
            if (!await _api.PutKeyringAsync(credentials, 0, keys.KeyringData, ct).ConfigureAwait(false))
                throw new SyncKeyException("Another device has just set this account's passphrase. Unlock with that passphrase instead.");
            _keys.Save(keys.MasterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keys.MasterKey);
        }
        await OnKeyReadyAsync().ConfigureAwait(false);
        return keys.RecoveryKey;
    }

    /// <exception cref="SyncKeyException">Wrong passphrase.</exception>
    public Task UnlockWithPassphraseAsync(string passphrase, CancellationToken ct = default) =>
        UnlockAsync(data => SyncKeyVault.UnlockWithPassphrase(data, passphrase), ct);

    /// <exception cref="SyncKeyException">Wrong recovery key.</exception>
    public Task UnlockWithRecoveryKeyAsync(string recoveryKey, CancellationToken ct = default) =>
        UnlockAsync(data => SyncKeyVault.UnlockWithRecoveryKey(data, recoveryKey), ct);

    /// <summary>Re-protects the account key with a new passphrase for every device that joins later.</summary>
    public async Task ChangePassphraseAsync(string newPassphrase, CancellationToken ct = default)
    {
        var credentials = RequireCredentials();
        var master = _keys.Load() ?? throw new InvalidOperationException("Unlock sync on this device first.");
        try
        {
            var current = await _api.GetKeyringAsync(credentials, ct).ConfigureAwait(false)
                ?? throw new SyncKeyException("This account has no passphrase yet.");
            var updated = await Task.Run(() => SyncKeyVault.ChangePassphrase(current.Data, master, newPassphrase), ct).ConfigureAwait(false);
            if (!await _api.PutKeyringAsync(credentials, current.Version, updated, ct).ConfigureAwait(false))
                throw new SyncKeyException("Another device changed the passphrase at the same time. Try again.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    public Task<SyncAccountInfo> GetAccountAsync(CancellationToken ct = default) => _api.GetAccountAsync(RequireCredentials(), ct);

    public Task<IReadOnlyList<SyncDeviceToken>> ListDevicesAsync(CancellationToken ct = default) => _api.ListTokensAsync(RequireCredentials(), ct);

    /// <summary>A token for another device; the plaintext is returned only here.</summary>
    public Task<(SyncDeviceToken Device, string Token)> AddDeviceAsync(string name, int? expiresInDays = null, CancellationToken ct = default) =>
        _api.CreateTokenAsync(RequireCredentials(), name, expiresInDays, ct: ct);

    public Task RevokeDeviceAsync(string tokenId, CancellationToken ct = default) => _api.RevokeTokenAsync(RequireCredentials(), tokenId, ct);

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
        StageChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task UnlockAsync(Func<string, byte[]> unlock, CancellationToken ct)
    {
        var keyring = await _api.GetKeyringAsync(RequireCredentials(), ct).ConfigureAwait(false)
            ?? throw new SyncKeyException("This account has no passphrase yet. Create one on this device.");
        var master = await Task.Run(() => unlock(keyring.Data), ct).ConfigureAwait(false);
        try
        {
            _keys.Save(master);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
        await OnKeyReadyAsync().ConfigureAwait(false);
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
        _engine.ReloadKey();
        StageChanged?.Invoke(this, EventArgs.Empty);
        await _engine.SyncNowAsync().ConfigureAwait(false);
    }

    private SyncCredentials RequireCredentials() =>
        _credentials.Load() ?? throw new InvalidOperationException("Connect this device to a sync account first.");
}
