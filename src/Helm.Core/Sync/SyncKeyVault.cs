using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Konscious.Security.Cryptography;

namespace Helm.Core.Sync;

/// <summary>The passphrase or recovery key does not open this keyring, or a new passphrase is too weak.</summary>
public sealed class SyncKeyException(string message) : Exception(message);

/// <summary>The account keys, the keyring to upload, and the recovery key to show the user exactly once.</summary>
public sealed record NewSyncKeys(SyncKeySet Keys, string KeyringData, string RecoveryKey);

public enum PassphraseStrength
{
    TooShort,
    Weak,
    Fair,
    Strong,
}

/// <summary>
/// The account's keyring, stored on the server as an opaque string (/v1/keyring). It holds the current master key
/// wrapped twice — by the passphrase and by the recovery key — plus every older master key wrapped by the current
/// one, so data sealed before a rotation stays readable. The server never sees a passphrase, recovery key or master.
/// <list type="bullet">
/// <item>Passphrase KEK = Argon2id(passphrase, 16-byte salt, 128 MiB, 3 passes, 4 lanes) — v2. Keyrings from Helm
/// 0.5.0 (v1) used PBKDF2-HMAC-SHA256 with 600 000 iterations; they still open, and are upgraded on unlock.</item>
/// <item>Recovery KEK = HKDF-SHA256(32 random bytes, info "helm-sync/v1/recovery"); shown as Crockford base32.</item>
/// <item>Each wrap = AES-256-GCM [nonce 12][tag 16][ciphertext 32], AAD naming its slot and epoch.</item>
/// </list>
/// </summary>
public static class SyncKeyVault
{
    public const int MinPassphraseLength = 14;
    internal static readonly KdfParams DefaultKdf = new("argon2id", 0, 128 * 1024, 3, 4);
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string RecoveryPrefix = "HELM-";

    /// <summary>First device of an account. Slow on purpose (Argon2id): call it off the UI thread.</summary>
    public static NewSyncKeys Create(string passphrase) => Create(passphrase, DefaultKdf);

    internal static NewSyncKeys Create(string passphrase, KdfParams kdf)
    {
        ValidateNewPassphrase(passphrase);
        var master = SyncKeyring.CreateMasterKey();
        var (recoverySecret, recoveryText) = NewRecoverySecret();
        var salt = RandomNumberGenerator.GetBytes(16);
        var document = new KeyringJson(2, 1, kdf.WithSalt(salt),
            Wrap(PassphraseKek(passphrase, kdf, salt), master, PassphraseAad(1)),
            Wrap(RecoveryKek(recoverySecret), master, RecoveryAad(1)), []);
        CryptographicOperations.ZeroMemory(recoverySecret);
        return new NewSyncKeys(SyncKeySet.Single(master), Serialize(document), recoveryText);
    }

    /// <exception cref="SyncKeyException">Wrong passphrase, or a damaged or foreign keyring.</exception>
    public static SyncKeySet UnlockWithPassphrase(string keyringData, string passphrase)
    {
        var document = Parse(keyringData);
        var master = Unwrap(PassphraseKek(passphrase, document.Kdf, document.Kdf.Salt), document.Passphrase,
            PassphraseAad(document.Epoch), "The passphrase is not correct.");
        return Expand(document, master);
    }

    /// <exception cref="SyncKeyException">Wrong or mistyped recovery key.</exception>
    public static SyncKeySet UnlockWithRecoveryKey(string keyringData, string recoveryKey)
    {
        var document = Parse(keyringData);
        var secret = ParseRecoveryKey(recoveryKey) ?? throw new SyncKeyException("That is not a Helm recovery key.");
        try
        {
            var master = Unwrap(RecoveryKek(secret), document.Recovery, RecoveryAad(document.Epoch),
                "The recovery key does not match this account.");
            return Expand(document, master);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>True for keyrings still protected by PBKDF2 (Helm 0.5.0), which <see cref="Upgrade"/> moves to Argon2id.</summary>
    public static bool NeedsUpgrade(string keyringData) => Parse(keyringData).Kdf.Alg != DefaultKdf.Alg;

    public static int EpochOf(string keyringData) => Parse(keyringData).Epoch;

    /// <summary>Re-wraps the passphrase slot with Argon2id. Checks the passphrase first; the recovery key is kept.</summary>
    /// <exception cref="SyncKeyException">Wrong passphrase.</exception>
    public static string Upgrade(string keyringData, string passphrase) => Upgrade(keyringData, passphrase, DefaultKdf);

    internal static string Upgrade(string keyringData, string passphrase, KdfParams kdf)
    {
        var document = Parse(keyringData);
        using var keys = UnlockWithPassphrase(keyringData, passphrase);
        var salt = RandomNumberGenerator.GetBytes(16);
        return Serialize(document with
        {
            V = 2,
            Kdf = kdf.WithSalt(salt),
            Passphrase = Wrap(PassphraseKek(passphrase, kdf, salt), keys.Current, PassphraseAad(document.Epoch)),
        });
    }

    /// <summary>Re-wraps the current master key under a new passphrase; the recovery key and older keys are kept.</summary>
    public static string ChangePassphrase(string keyringData, SyncKeySet keys, string newPassphrase)
    {
        ValidateNewPassphrase(newPassphrase);
        var document = Parse(keyringData);
        if (keys.CurrentEpoch != document.Epoch) throw new SyncKeyException("The account key changed; unlock again first.");
        var salt = RandomNumberGenerator.GetBytes(16);
        return Serialize(document with
        {
            V = 2,
            Kdf = DefaultKdf.WithSalt(salt),
            Passphrase = Wrap(PassphraseKek(newPassphrase, DefaultKdf, salt), keys.Current, PassphraseAad(document.Epoch)),
        });
    }

    /// <summary>
    /// Key rotation (after removing a device): a new master key for the next epoch, wrapped by the same passphrase and a
    /// NEW recovery key; every older key is re-wrapped under the new one. The removed device knows only old keys.
    /// </summary>
    /// <exception cref="SyncKeyException">Wrong passphrase.</exception>
    public static NewSyncKeys Rotate(string keyringData, string passphrase) => Rotate(keyringData, passphrase, DefaultKdf);

    internal static NewSyncKeys Rotate(string keyringData, string passphrase, KdfParams kdf)
    {
        var document = Parse(keyringData);
        using var old = UnlockWithPassphrase(keyringData, passphrase);
        var epoch = document.Epoch + 1;
        var master = SyncKeyring.CreateMasterKey();
        var (recoverySecret, recoveryText) = NewRecoverySecret();
        var salt = RandomNumberGenerator.GetBytes(16);
        var previous = old.Keys.Select(k => new PreviousJson(k.Key, Wrap(master, k.Value, PreviousAad(epoch, k.Key)))).ToList();
        var rotated = new KeyringJson(2, epoch, kdf.WithSalt(salt),
            Wrap(PassphraseKek(passphrase, kdf, salt), master, PassphraseAad(epoch)),
            Wrap(RecoveryKek(recoverySecret), master, RecoveryAad(epoch)), previous);
        CryptographicOperations.ZeroMemory(recoverySecret);

        var all = old.Keys.ToDictionary(k => k.Key, k => k.Value.ToArray());
        all[epoch] = master;
        return new NewSyncKeys(new SyncKeySet(epoch, all), Serialize(rotated), recoveryText);
    }

    /// <exception cref="SyncKeyException">Shorter than <see cref="MinPassphraseLength"/> or obviously weak.</exception>
    public static void ValidateNewPassphrase(string passphrase)
    {
        switch (EstimateStrength(passphrase))
        {
            case PassphraseStrength.TooShort:
                throw new SyncKeyException($"Use a passphrase of at least {MinPassphraseLength} characters.");
            case PassphraseStrength.Weak:
                throw new SyncKeyException("This passphrase is too easy to guess. Use several unrelated words, or mix in digits and symbols.");
        }
    }

    /// <summary>
    /// A rough, conservative estimate: character pool × length, with repeated characters and runs discounted.
    /// Weak &lt; 60 bits, Fair &lt; 80 bits, Strong ≥ 80 bits. Four or five random words are Strong.
    /// </summary>
    public static PassphraseStrength EstimateStrength(string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < MinPassphraseLength) return PassphraseStrength.TooShort;
        var pool = 0;
        if (passphrase.Any(char.IsLower)) pool += 26;
        if (passphrase.Any(char.IsUpper)) pool += 26;
        if (passphrase.Any(char.IsDigit)) pool += 10;
        if (passphrase.Any(c => !char.IsLetterOrDigit(c))) pool += 33;
        if (pool == 0) pool = 26;

        // Count only "surprising" characters: not a repeat of, or one step from, the previous one (aaaa, abcd, 1234).
        var effective = 1;
        for (var i = 1; i < passphrase.Length; i++)
        {
            var step = passphrase[i] - passphrase[i - 1];
            if (step is not (0 or 1 or -1)) effective++;
        }
        var distinct = passphrase.Distinct().Count();
        effective = Math.Min(effective, distinct * 3);
        var bits = effective * Math.Log2(pool);
        return bits < 60 ? PassphraseStrength.Weak : bits < 80 ? PassphraseStrength.Fair : PassphraseStrength.Strong;
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

    private static (byte[] Secret, string Text) NewRecoverySecret()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        return (secret, FormatRecoveryKey(secret));
    }

    private static SyncKeySet Expand(KeyringJson document, byte[] master)
    {
        var keys = new Dictionary<int, byte[]> { [document.Epoch] = master };
        foreach (var previous in document.Previous)
        {
            keys[previous.Epoch] = Unwrap(master.ToArray(), previous.Wrap, PreviousAad(document.Epoch, previous.Epoch),
                "The keyring on the server is damaged.");
        }
        return new SyncKeySet(document.Epoch, keys);
    }

    private static byte[] PassphraseKek(string passphrase, KdfParams kdf, byte[] salt)
    {
        var secret = Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));
        try
        {
            switch (kdf.Alg)
            {
                case "pbkdf2-sha256" when kdf.Iterations is >= 100_000 and <= 10_000_000:
                    return Rfc2898DeriveBytes.Pbkdf2(secret, salt, kdf.Iterations, HashAlgorithmName.SHA256, SyncKeyring.KeySize);
                case "argon2id" when kdf.MemoryKib is >= 8 * 1024 and <= 1024 * 1024 && kdf.Passes is >= 1 and <= 20 && kdf.Lanes is >= 1 and <= 16:
                    using (var argon = new Argon2id(secret) { Salt = salt, MemorySize = kdf.MemoryKib, Iterations = kdf.Passes, DegreeOfParallelism = kdf.Lanes })
                        return argon.GetBytes(SyncKeyring.KeySize);
                default:
                    throw new SyncKeyException("This keyring was made by a newer version of Helm.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] RecoveryKek(byte[] secret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, SyncKeyring.KeySize, salt: [], info: Encoding.UTF8.GetBytes("helm-sync/v1/recovery"));

    // Epoch 1 keeps the exact AAD of Helm 0.5.0, so its keyrings (and their recovery slot) still open.
    private static byte[] PassphraseAad(int epoch) =>
        Encoding.UTF8.GetBytes(epoch == 1 ? "helm-sync/v1/keyring|passphrase" : $"helm-sync/v2/keyring|passphrase|{epoch}");

    private static byte[] RecoveryAad(int epoch) =>
        Encoding.UTF8.GetBytes(epoch == 1 ? "helm-sync/v1/keyring|recovery" : $"helm-sync/v2/keyring|recovery|{epoch}");

    private static byte[] PreviousAad(int currentEpoch, int epoch) =>
        Encoding.UTF8.GetBytes($"helm-sync/v2/keyring|previous|{currentEpoch}|{epoch}");

    private static byte[] Wrap(byte[] kek, byte[] master, byte[] aad)
    {
        var output = new byte[12 + 16 + master.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using (var gcm = new AesGcm(kek, 16))
            gcm.Encrypt(output.AsSpan(0, 12), master, output.AsSpan(28), output.AsSpan(12, 16), aad);
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
        KeyringJson? document;
        try
        {
            document = JsonSerializer.Deserialize<KeyringJson>(data, JsonOptions);
        }
        catch (JsonException)
        {
            throw new SyncKeyException("The keyring on the server is damaged.");
        }
        if (document is null || document.V is < 1 or > 2 || document.Kdf is null || document.Passphrase is null || document.Recovery is null)
            throw new SyncKeyException("This keyring was made by a newer version of Helm.");
        // v1 (Helm 0.5.0) has no epoch and no previous keys.
        return document with { Epoch = Math.Max(1, document.Epoch), Previous = document.Previous ?? [] };
    }

    private static string Serialize(KeyringJson document) => JsonSerializer.Serialize(document, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    internal sealed record KdfParams(
        string Alg,
        [property: JsonPropertyName("iter")] int Iterations,
        [property: JsonPropertyName("m")] int MemoryKib,
        [property: JsonPropertyName("t")] int Passes,
        [property: JsonPropertyName("p")] int Lanes,
        byte[]? SaltBytes = null)
    {
        [JsonPropertyName("salt")]
        public byte[] Salt { get => SaltBytes ?? []; init => SaltBytes = value; }

        public KdfParams WithSalt(byte[] salt) => this with { SaltBytes = salt };
    }

    private sealed record PreviousJson(int Epoch, byte[] Wrap);

    private sealed record KeyringJson(
        int V,
        int Epoch,
        KdfParams Kdf,
        [property: JsonPropertyName("pass")] byte[] Passphrase,
        [property: JsonPropertyName("rec")] byte[] Recovery,
        [property: JsonPropertyName("prev")] IReadOnlyList<PreviousJson> Previous);
}
