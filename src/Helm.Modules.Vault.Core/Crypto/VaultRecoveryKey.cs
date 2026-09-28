using System.Security.Cryptography;
using System.Text;

namespace Helm.Modules.Vault.Crypto;

/// <summary>
/// The vault recovery key: 32 random bytes shown once as <c>HELMV-XXXX-…</c> (Crockford base32, 52 characters, plus a
/// 4-character checksum so a typo is caught before any decryption). Its <see cref="IdOf"/> is printed on the
/// Emergency Kit and stored in the keyring, so Helm can tell whether the kit the user kept is the current one.
/// </summary>
public static class VaultRecoveryKey
{
    public const string Prefix = "HELMV-";
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int SecretSize = 32;
    private const int DataChars = 52; // ceil(256 / 5)
    private const int CheckChars = 4;

    public static (byte[] Secret, string Text) Create()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretSize);
        return (secret, Format(secret));
    }

    public static string Format(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretSize) throw new ArgumentException("A recovery secret is 32 bytes.", nameof(secret));
        var chars = Encode(secret) + Checksum(secret);
        var groups = Enumerable.Range(0, chars.Length / 4).Select(i => chars.Substring(i * 4, 4));
        return Prefix + string.Join('-', groups);
    }

    /// <summary>Accepts any case, spaces or dashes, and the usual look-alikes (O for 0, I and L for 1).</summary>
    /// <returns>Null when this is not a vault recovery key or it was mistyped (checksum mismatch).</returns>
    public static byte[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = new StringBuilder();
        var body = text.Trim().ToUpperInvariant();
        if (body.StartsWith("HELMV", StringComparison.Ordinal)) body = body[5..];
        foreach (var c in body)
        {
            if (c is '-' or ' ' or '\t' or '\r' or '\n') continue;
            normalized.Append(c switch { 'O' => '0', 'I' or 'L' => '1', _ => c });
        }
        if (normalized.Length != DataChars + CheckChars) return null;
        var data = normalized.ToString(0, DataChars);
        var secret = Decode(data);
        if (secret is null) return null;
        if (Checksum(secret) != normalized.ToString(DataChars, CheckChars))
        {
            CryptographicOperations.ZeroMemory(secret);
            return null;
        }
        return secret;
    }

    /// <summary>A short public fingerprint of the recovery key, e.g. <c>7F3A-91C2</c>. It reveals nothing about the key.</summary>
    public static string IdOf(ReadOnlySpan<byte> secret)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData([.. "helm-vault/v1/recovery-id|"u8, .. secret], hash);
        var id = Encode(hash[..5]);
        return $"{id[..4]}-{id[4..8]}";
    }

    private static string Checksum(ReadOnlySpan<byte> secret)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(secret, hash);
        return Encode(hash[..3])[..CheckChars];
    }

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Crockford[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) output.Append(Crockford[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    private static byte[]? Decode(string chars)
    {
        var output = new byte[SecretSize];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in chars)
        {
            var value = Crockford.IndexOf(c);
            if (value < 0) return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                if (index == SecretSize) return null;
                output[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }
        // 52 characters carry 260 bits: the 4 padding bits must be zero.
        return index == SecretSize && (buffer & ((1 << bits) - 1)) == 0 ? output : null;
    }
}
