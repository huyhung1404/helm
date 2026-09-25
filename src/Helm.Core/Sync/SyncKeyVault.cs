using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helm.Core.Sync;

/// <summary>The passphrase or recovery key does not open this keyring.</summary>
public sealed class SyncKeyException(string message) : Exception(message);

/// <summary>A new master key, the keyring to upload, and the recovery key to show the user exactly once.</summary>
public sealed record NewSyncKeys(byte[] MasterKey, string KeyringData, string RecoveryKey);

/// <summary>
/// The account's keyring: the master key wrapped twice, so any device can unlock it with the passphrase, or with the
/// recovery key when the passphrase is forgotten. Stored on the server as an opaque string (/v1/keyring); the server
/// never sees the passphrase, the recovery key or the master key.
/// <list type="bullet">
/// <item>Passphrase KEK = PBKDF2-HMAC-SHA256(passphrase, 16-byte salt, 600 000 iterations), per OWASP 2023.</item>
/// <item>Recovery KEK = HKDF-SHA256(32 random bytes, info "helm-sync/v1/recovery"); shown as Crockford base32.</item>
/// <item>Each wrap = AES-256-GCM [nonce 12][tag 16][ciphertext 32] with AAD naming its slot.</item>
/// </list>
/// </summary>
public static class SyncKeyVault
{
    public const int MinPassphraseLength = 10;
    internal const int Iterations = 600_000;
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string RecoveryPrefix = "HELM-";
    private static readonly byte[] PassphraseAad = Encoding.UTF8.GetBytes("helm-sync/v1/keyring|passphrase");
    private static readonly byte[] RecoveryAad = Encoding.UTF8.GetBytes("helm-sync/v1/keyring|recovery");

    /// <summary>Slow on purpose (about a second): call it off the UI thread.</summary>
    public static NewSyncKeys Create(string passphrase) => Create(passphrase, Iterations);

    internal static NewSyncKeys Create(string passphrase, int iterations)
    {
        ValidatePassphrase(passphrase);
        var master = SyncKeyring.CreateMasterKey();
        var recoverySecret = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);

        var document = new KeyringJson(1, new KdfJson("pbkdf2-sha256", iterations, salt),
            Wrap(PassphraseKey(passphrase, salt, iterations), master, PassphraseAad),
            Wrap(RecoveryKek(recoverySecret), master, RecoveryAad));
        var recovery = FormatRecoveryKey(recoverySecret);
        CryptographicOperations.ZeroMemory(recoverySecret);
        return new NewSyncKeys(master, JsonSerializer.Serialize(document, JsonOptions), recovery);
    }

    /// <exception cref="SyncKeyException">Wrong passphrase, or a damaged or foreign keyring.</exception>
    public static byte[] UnlockWithPassphrase(string keyringData, string passphrase)
    {
        var document = Parse(keyringData);
        if (document.Kdf.Alg != "pbkdf2-sha256" || document.Kdf.Iterations is < 100_000 or > 10_000_000)
            throw new SyncKeyException("This keyring was made by a newer version of Helm.");
        return Unwrap(PassphraseKey(passphrase, document.Kdf.Salt, document.Kdf.Iterations), document.Passphrase, PassphraseAad,
            "The passphrase is not correct.");
    }

    /// <exception cref="SyncKeyException">Wrong or mistyped recovery key.</exception>
    public static byte[] UnlockWithRecoveryKey(string keyringData, string recoveryKey)
    {
        var document = Parse(keyringData);
        var secret = ParseRecoveryKey(recoveryKey) ?? throw new SyncKeyException("That is not a Helm recovery key.");
        try
        {
            return Unwrap(RecoveryKek(secret), document.Recovery, RecoveryAad, "The recovery key does not match this account.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Re-wraps the same master key under a new passphrase, keeping the recovery key.</summary>
    public static string ChangePassphrase(string keyringData, byte[] masterKey, string newPassphrase)
    {
        ValidatePassphrase(newPassphrase);
        var document = Parse(keyringData);
        var salt = RandomNumberGenerator.GetBytes(16);
        var updated = document with
        {
            Kdf = new KdfJson("pbkdf2-sha256", Iterations, salt),
            Passphrase = Wrap(PassphraseKey(newPassphrase, salt, Iterations), masterKey, PassphraseAad),
        };
        return JsonSerializer.Serialize(updated, JsonOptions);
    }

    public static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < MinPassphraseLength)
            throw new SyncKeyException($"Use a passphrase of at least {MinPassphraseLength} characters.");
    }

    /// <summary>"HELM-XXXX-XXXX-…" (52 base32 chars in groups of four).</summary>
    internal static string FormatRecoveryKey(ReadOnlySpan<byte> secret)
    {
        var chars = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in secret)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars.Append(Crockford[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) chars.Append(Crockford[(buffer << (5 - bits)) & 31]);
        var groups = Enumerable.Range(0, (chars.Length + 3) / 4).Select(i => chars.ToString(i * 4, Math.Min(4, chars.Length - i * 4)));
        return RecoveryPrefix + string.Join('-', groups);
    }

    /// <summary>Accepts any case, spaces or dashes, and the usual Crockford look-alikes (O→0, I/L→1).</summary>
    internal static byte[]? ParseRecoveryKey(string text)
    {
        var cleaned = new string(text.ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        // "HELM" is never part of the key: its E is valid base32 but its L would read as 1.
        if (cleaned.StartsWith("HELM", StringComparison.Ordinal)) cleaned = cleaned[4..];
        cleaned = new string(cleaned.Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToArray());
        if (cleaned.Length != 52) return null;

        var bytes = new List<byte>(32);
        int buffer = 0, bits = 0;
        foreach (var c in cleaned)
        {
            var value = Crockford.IndexOf(c);
            if (value < 0) return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return bytes.Count == 32 ? bytes.ToArray() : null;
    }

    private static byte[] PassphraseKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)), salt, iterations,
            HashAlgorithmName.SHA256, SyncKeyring.KeySize);

    private static byte[] RecoveryKek(byte[] secret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, SyncKeyring.KeySize, salt: [], info: Encoding.UTF8.GetBytes("helm-sync/v1/recovery"));

    private static byte[] Wrap(byte[] kek, byte[] master, byte[] aad)
    {
        var output = new byte[12 + 16 + master.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using (var gcm = new AesGcm(kek, 16))
            gcm.Encrypt(output.AsSpan(0, 12), master, output.AsSpan(28), output.AsSpan(12, 16), aad);
        CryptographicOperations.ZeroMemory(kek);
        return output;
    }

    private static byte[] Unwrap(byte[] kek, byte[] wrapped, byte[] aad, string failure)
    {
        try
        {
            if (wrapped.Length != 12 + 16 + SyncKeyring.KeySize) throw new SyncKeyException("The keyring is damaged.");
            var master = new byte[SyncKeyring.KeySize];
            using var gcm = new AesGcm(kek, 16);
            gcm.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28), wrapped.AsSpan(12, 16), master, aad);
            return master;
        }
        catch (CryptographicException)
        {
            throw new SyncKeyException(failure);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static KeyringJson Parse(string data)
    {
        try
        {
            var document = JsonSerializer.Deserialize<KeyringJson>(data, JsonOptions);
            if (document is null || document.V != 1) throw new SyncKeyException("This keyring was made by a newer version of Helm.");
            return document;
        }
        catch (JsonException)
        {
            throw new SyncKeyException("The keyring on the server is damaged.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record KdfJson(string Alg, [property: JsonPropertyName("iter")] int Iterations, byte[] Salt);

    private sealed record KeyringJson(
        int V, KdfJson Kdf, [property: JsonPropertyName("pass")] byte[] Passphrase, [property: JsonPropertyName("rec")] byte[] Recovery);
}
