using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Helm.Modules.Vault.Crypto;

/// <summary>The vault password or recovery key does not open the keyring, or a keyring or item is damaged.</summary>
public sealed class VaultKeyException(string message) : Exception(message);

/// <summary>Argon2id parameters, stored with the keyring so they can be raised later without breaking old vaults.</summary>
public sealed record VaultKdf(string Alg, int MemoryKib, int Passes, int Lanes, byte[] Salt)
{
    public const string Argon2id = "argon2id";

    /// <summary>The sync passphrase's parameters: 128 MiB, 3 passes, 4 lanes (about a second on a PC).</summary>
    public static VaultKdf Default() => new(Argon2id, 128 * 1024, 3, 4, RandomNumberGenerator.GetBytes(16));

    /// <summary>Refuses parameters a newer build may write but this one would compute wrongly or too slowly.</summary>
    internal bool IsSupported =>
        Alg == Argon2id && MemoryKib is >= 8 * 1024 and <= 1024 * 1024 && Passes is >= 1 and <= 20 && Lanes is >= 1 and <= 16 && Salt.Length == 16;
}

/// <summary>
/// The primitives of the vault format (docs/vault-format.md): AES-256-GCM sealing as [nonce 12][tag 16][ciphertext]
/// with a text AAD, HKDF-SHA256 sub-keys, and Argon2id password keys. Nothing else is used.
/// </summary>
public static class VaultCrypto
{
    public const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const int Overhead = NonceSize + TagSize;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string aad)
    {
        var output = new byte[Overhead + plaintext.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(output.AsSpan(0, NonceSize), plaintext, output.AsSpan(Overhead), output.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(aad));
        return output;
    }

    /// <exception cref="CryptographicException">Wrong key, other AAD, or tampered data.</exception>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sealedData, string aad)
    {
        if (sealedData.Length < Overhead) throw new CryptographicException("Sealed data is too short.");
        var plaintext = new byte[sealedData.Length - Overhead];
        using var gcm = new AesGcm(key, TagSize);
        try
        {
            gcm.Decrypt(sealedData[..NonceSize], sealedData[Overhead..], sealedData.Slice(NonceSize, TagSize), plaintext, Encoding.UTF8.GetBytes(aad));
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        return plaintext;
    }

    /// <summary>A named sub-key of a 32-byte key; the caller zeroes it.</summary>
    public static byte[] Derive(ReadOnlySpan<byte> key, string info)
    {
        // The span overload: the input key is not copied into a new (unpinned) array.
        var output = new byte[KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, output, salt: [], info: Encoding.UTF8.GetBytes(info));
        return output;
    }

    /// <summary>Slow on purpose: call it off the UI thread.</summary>
    /// <exception cref="VaultKeyException">Parameters this build does not support (written by a newer Helm).</exception>
    public static byte[] PasswordKey(string password, VaultKdf kdf)
    {
        if (!kdf.IsSupported) throw new VaultKeyException("This vault was made by a newer version of Helm.");
        var secret = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try
        {
            using var argon = new Argon2id(secret) { Salt = kdf.Salt, MemorySize = kdf.MemoryKib, Iterations = kdf.Passes, DegreeOfParallelism = kdf.Lanes };
            return argon.GetBytes(KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
