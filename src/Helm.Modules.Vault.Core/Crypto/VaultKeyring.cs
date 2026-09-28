using System.Security.Cryptography;
using Helm.Core.Sync;

namespace Helm.Modules.Vault.Crypto;

/// <summary>
/// The synced keyring of one vault (collection <c>vault.keyring</c>, record <c>keyring-&lt;vaultId&gt;</c>): the vault
/// key wrapped by the vault password (Argon2id) and by the recovery key (HKDF). It holds no secret in clear, so it can
/// sit in the replica, on the server and in backups. See docs/vault-format.md.
/// </summary>
public sealed record VaultKeyringData
{
    public const int CurrentFormat = 1;

    public int V { get; init; } = CurrentFormat;

    /// <summary>Random id of the vault; every item names the vault whose key sealed it.</summary>
    public required string VaultId { get; init; }

    public int Epoch { get; init; } = 1;

    public long CreatedAtMs { get; init; }

    public long ChangedAtMs { get; init; }

    public required VaultKdf Kdf { get; init; }

    /// <summary>The vault key wrapped by the password key. AAD <c>helm-vault/v1/keyring|pass|&lt;vaultId&gt;|&lt;epoch&gt;</c>.</summary>
    public required byte[] Pass { get; init; }

    /// <summary>The vault key wrapped by the recovery key. AAD <c>helm-vault/v1/keyring|rec|&lt;vaultId&gt;|&lt;epoch&gt;</c>.</summary>
    public required byte[] Rec { get; init; }

    /// <summary><see cref="VaultRecoveryKey.IdOf"/> of the current recovery key, printed on the Emergency Kit.</summary>
    public required string RecoveryId { get; init; }

    /// <summary>
    /// <c>HKDF(vault key, "helm-vault/v1/key-check")</c>, first 16 bytes: tells whether a key from quick unlock or a
    /// backup is this vault's key without decrypting anything. Reveals nothing about the key.
    /// </summary>
    public required byte[] KeyCheck { get; init; }

    public bool Matches(VaultKey key) => CryptographicOperations.FixedTimeEquals(KeyCheck, VaultKeyring.KeyCheckOf(key));

    public static string RecordId(string vaultId) => "keyring-" + vaultId;
}

/// <summary>A new vault's keyring, its unlocked key, and the recovery key to show exactly once.</summary>
public sealed record NewVault(VaultKeyringData Keyring, VaultKey Key, string RecoveryKey);

/// <summary>Creating, opening and re-wrapping keyrings. Every function that takes a password is slow (Argon2id).</summary>
public static class VaultKeyring
{
    public const int MinPasswordLength = SyncKeyVault.MinPassphraseLength;

    /// <exception cref="VaultKeyException">The password is too weak (same rule as the sync passphrase).</exception>
    public static NewVault Create(string password, long nowMs) => Create(password, nowMs, VaultKdf.Default());

    internal static NewVault Create(string password, long nowMs, VaultKdf kdf)
    {
        ValidateNewPassword(password);
        var vaultId = SyncIds.NewId();
        var key = VaultKey.Create();
        var (secret, text) = VaultRecoveryKey.Create();
        try
        {
            var keyring = new VaultKeyringData
            {
                VaultId = vaultId,
                CreatedAtMs = nowMs,
                ChangedAtMs = nowMs,
                Kdf = kdf,
                Pass = WrapWithPassword(key, password, kdf, vaultId, 1),
                Rec = WrapWithRecovery(key, secret, vaultId, 1),
                RecoveryId = VaultRecoveryKey.IdOf(secret),
                KeyCheck = KeyCheckOf(key),
            };
            Verify(keyring, key, password, text);
            return new NewVault(keyring, key, text);
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <exception cref="VaultKeyException">Wrong password, or a keyring from a newer Helm.</exception>
    public static VaultKey UnlockWithPassword(VaultKeyringData keyring, string password)
    {
        EnsureSupported(keyring);
        var kek = VaultCrypto.PasswordKey(password, keyring.Kdf);
        return Unwrap(kek, keyring.Pass, Aad("pass", keyring), "The vault password is not correct.");
    }

    /// <exception cref="VaultKeyException">Not a recovery key, mistyped, or the key of another vault or an older kit.</exception>
    public static VaultKey UnlockWithRecoveryKey(VaultKeyringData keyring, string recoveryKey)
    {
        EnsureSupported(keyring);
        var secret = VaultRecoveryKey.Parse(recoveryKey) ?? throw new VaultKeyException("That is not a vault recovery key, or it has a typo.");
        try
        {
            if (VaultRecoveryKey.IdOf(secret) != keyring.RecoveryId)
                throw new VaultKeyException("This recovery key belongs to another vault or to an older Emergency Kit.");
            return Unwrap(RecoveryKek(secret), keyring.Rec, Aad("rec", keyring), "This recovery key does not open the vault.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>New password, same vault key and recovery key. The result is checked with the new password first.</summary>
    public static VaultKeyringData ChangePassword(VaultKeyringData keyring, VaultKey key, string newPassword, long nowMs)
    {
        EnsureSupported(keyring);
        ValidateNewPassword(newPassword);
        var kdf = VaultKdf.Default() with { MemoryKib = keyring.Kdf.MemoryKib, Passes = keyring.Kdf.Passes, Lanes = keyring.Kdf.Lanes };
        var changed = keyring with
        {
            Kdf = kdf,
            Pass = WrapWithPassword(key, newPassword, kdf, keyring.VaultId, keyring.Epoch),
            ChangedAtMs = nowMs,
        };
        using (var check = UnlockWithPassword(changed, newPassword))
        {
            if (!check.Bytes.SequenceEqual(key.Bytes)) throw new VaultKeyException("The new keyring did not open with the new password.");
        }
        return changed;
    }

    /// <summary>A new recovery key (the old kit stops working); the result is checked with it first.</summary>
    public static (VaultKeyringData Keyring, string RecoveryKey) NewRecoveryKey(VaultKeyringData keyring, VaultKey key, long nowMs)
    {
        EnsureSupported(keyring);
        var (secret, text) = VaultRecoveryKey.Create();
        try
        {
            var changed = keyring with
            {
                Rec = WrapWithRecovery(key, secret, keyring.VaultId, keyring.Epoch),
                RecoveryId = VaultRecoveryKey.IdOf(secret),
                ChangedAtMs = nowMs,
            };
            using (var check = UnlockWithRecoveryKey(changed, text))
            {
                if (!check.Bytes.SequenceEqual(key.Bytes)) throw new VaultKeyException("The new keyring did not open with the new recovery key.");
            }
            return (changed, text);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static void ValidateNewPassword(string password)
    {
        try
        {
            SyncKeyVault.ValidateNewPassphrase(password);
        }
        catch (SyncKeyException ex)
        {
            throw new VaultKeyException(ex.Message.Replace("passphrase", "password", StringComparison.OrdinalIgnoreCase));
        }
    }

    internal static byte[] KeyCheckOf(VaultKey key)
    {
        var check = key.Derive("helm-vault/v1/key-check");
        try
        {
            return check[..16];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(check);
        }
    }

    private static void Verify(VaultKeyringData keyring, VaultKey key, string password, string recoveryKey)
    {
        using var byPassword = UnlockWithPassword(keyring, password);
        using var byRecovery = UnlockWithRecoveryKey(keyring, recoveryKey);
        if (!byPassword.Bytes.SequenceEqual(key.Bytes) || !byRecovery.Bytes.SequenceEqual(key.Bytes))
            throw new VaultKeyException("The new keyring did not open.");
    }

    private static void EnsureSupported(VaultKeyringData keyring)
    {
        if (keyring.V != VaultKeyringData.CurrentFormat) throw new VaultKeyException("This vault was made by a newer version of Helm.");
    }

    private static byte[] WrapWithPassword(VaultKey key, string password, VaultKdf kdf, string vaultId, int epoch)
    {
        var kek = VaultCrypto.PasswordKey(password, kdf);
        try
        {
            return VaultCrypto.Seal(kek, key.Bytes, Aad("pass", vaultId, epoch));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] WrapWithRecovery(VaultKey key, byte[] secret, string vaultId, int epoch)
    {
        var kek = RecoveryKek(secret);
        try
        {
            return VaultCrypto.Seal(kek, key.Bytes, Aad("rec", vaultId, epoch));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] RecoveryKek(byte[] secret) => VaultCrypto.Derive(secret, "helm-vault/v1/recovery");

    private static VaultKey Unwrap(byte[] kek, byte[] wrapped, string aad, string failure)
    {
        byte[]? raw = null;
        try
        {
            raw = VaultCrypto.Open(kek, wrapped, aad);
            if (raw.Length != VaultCrypto.KeySize) throw new VaultKeyException("The vault keyring is damaged.");
            return new VaultKey(raw);
        }
        catch (CryptographicException)
        {
            throw new VaultKeyException(failure);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            if (raw is not null) CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static string Aad(string slot, VaultKeyringData keyring) => Aad(slot, keyring.VaultId, keyring.Epoch);

    private static string Aad(string slot, string vaultId, int epoch) => $"helm-vault/v1/keyring|{slot}|{vaultId}|{epoch}";
}
