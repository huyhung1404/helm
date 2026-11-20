using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Helm.Core.Sync;

/// <summary>What a signed-in device sends a new one it pairs with: the new device's own token and the account keys.</summary>
public sealed record SyncPairingPackage(string Token, byte[] Keys);

/// <summary>
/// Pairing a new device by a code (docs/sync-protocol.md, "Pairing"), so it joins sync without anyone typing the
/// passphrase on it. The new device makes a P-256 key pair kept in memory only; the code is 18 digits derived from the
/// hash of its public key, so a server (or anyone between) that swapped in its own key would have to find another key
/// with the same 60 bits within the 5 minutes a pairing lives. A signed-in device that typed the code checks that the
/// public key matches it, then seals the package to that key (ECDH with a fresh key, HKDF-SHA256, AES-256-GCM): the
/// server relays only a public key and ciphertext.
/// </summary>
public static class SyncPairing
{
    public const int CodeDigits = 18;
    private const ulong CodeSpace = 1_000_000_000_000_000_000UL;
    private const byte Format = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] CodeLabel = "helm-pair/v1/code|"u8.ToArray();
    private static readonly byte[] IdLabel = "helm-pair/v1/id|"u8.ToArray();
    private static readonly byte[] SealInfo = "helm-pair/v1/seal"u8.ToArray();

    /// <summary>The 18 digits that name a public key (SubjectPublicKeyInfo DER).</summary>
    public static string CodeOf(ReadOnlySpan<byte> publicKey)
    {
        var hash = SHA256.HashData(Concat(CodeLabel, publicKey));
        var value = BinaryPrimitives.ReadUInt64BigEndian(hash) % CodeSpace;
        return value.ToString("D18", CultureInfo.InvariantCulture);
    }

    /// <summary>The server's handle on a pairing: SHA-256 of the code, lowercase hex.</summary>
    public static string IdOf(string code) => Convert.ToHexString(SHA256.HashData(Concat(IdLabel, Encoding.ASCII.GetBytes(code)))).ToLowerInvariant();

    /// <summary>A code as typed (spaces, dashes and dots allowed between digits) as 18 digits, or null.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null) return null;
        var digits = new StringBuilder(CodeDigits);
        foreach (var c in typed)
        {
            if (c is >= '0' and <= '9') digits.Append(c);
            else if (c is not (' ' or '-' or '.' or ' ')) return null;
        }
        return digits.Length == CodeDigits ? digits.ToString() : null;
    }

    /// <summary>A code in groups of three, as shown: "482 913 507 226 118 034".</summary>
    public static string FormatCode(string code) =>
        string.Join(' ', Enumerable.Range(0, code.Length / 3).Select(i => code.Substring(i * 3, 3)));

    public static bool Matches(string code, ReadOnlySpan<byte> publicKey) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeOf(publicKey)), Encoding.ASCII.GetBytes(code));

    /// <summary>Seals <paramref name="package"/> to the new device's public key, bound to the pairing id. Base64.</summary>
    /// <exception cref="SyncKeyException">The public key is not a P-256 key.</exception>
    public static string Seal(ReadOnlySpan<byte> publicKey, string id, SyncPairingPackage package)
    {
        using var recipient = ImportPublicKey(publicKey);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        var key = DeriveKey(ephemeral, recipient.PublicKey, ephemeralPublic, publicKey);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Plain(Format, package.Token, Convert.ToBase64String(package.Keys)));
        try
        {
            var output = new byte[1 + 2 + ephemeralPublic.Length + NonceSize + TagSize + plaintext.Length];
            output[0] = Format;
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(1), (ushort)ephemeralPublic.Length);
            ephemeralPublic.CopyTo(output, 3);
            var at = 3 + ephemeralPublic.Length;
            var nonce = output.AsSpan(at, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, output.AsSpan(at + NonceSize + TagSize), output.AsSpan(at + NonceSize, TagSize), Aad(id));
            return Convert.ToBase64String(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Opens a package sealed to <paramref name="offer"/>'s key for its pairing id.</summary>
    /// <exception cref="SyncKeyException">Damaged, for another pairing, or not sealed to this key.</exception>
    public static SyncPairingPackage Open(SyncPairingOffer offer, string sealedPackage)
    {
        const string broken = "The package from the other device could not be opened. Start pairing again.";
        byte[] data;
        try
        {
            data = Convert.FromBase64String(sealedPackage);
        }
        catch (FormatException)
        {
            throw new SyncKeyException(broken);
        }
        if (data.Length < 3 || data[0] != Format) throw new SyncKeyException(broken);
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(1));
        var at = 3 + keyLength;
        if (data.Length < at + NonceSize + TagSize) throw new SyncKeyException(broken);
        var ephemeralPublic = data.AsSpan(3, keyLength);
        using var sender = ImportPublicKey(ephemeralPublic, broken);
        var key = DeriveKey(offer.Key, sender.PublicKey, ephemeralPublic, offer.PublicKey);
        var plaintext = new byte[data.Length - at - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.AsSpan(at, NonceSize), data.AsSpan(at + NonceSize + TagSize), data.AsSpan(at + NonceSize, TagSize), plaintext, Aad(offer.Id));
            var plain = JsonSerializer.Deserialize<Plain>(plaintext);
            if (plain is null || plain.V != Format || string.IsNullOrEmpty(plain.Token) || string.IsNullOrEmpty(plain.Keys)) throw new SyncKeyException(broken);
            return new SyncPairingPackage(plain.Token, Convert.FromBase64String(plain.Keys));
        }
        catch (Exception e) when (e is AuthenticationTagMismatchException or CryptographicException or JsonException or FormatException)
        {
            throw new SyncKeyException(broken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static ECDiffieHellman ImportPublicKey(ReadOnlySpan<byte> publicKey, string message = "The other device's key is not a valid pairing key.")
    {
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (read != publicKey.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new SyncKeyException(message);
            return key;
        }
        catch (CryptographicException)
        {
            key.Dispose();
            throw new SyncKeyException(message);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static byte[] DeriveKey(ECDiffieHellman own, ECDiffieHellmanPublicKey other, ReadOnlySpan<byte> ephemeralPublic, ReadOnlySpan<byte> recipientPublic)
    {
        var shared = own.DeriveKeyFromHash(other, HashAlgorithmName.SHA256);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Concat(ephemeralPublic, recipientPublic), SealInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var all = new byte[a.Length + b.Length];
        a.CopyTo(all);
        b.CopyTo(all.AsSpan(a.Length));
        return all;
    }

    private static byte[] Aad(string id) => Encoding.ASCII.GetBytes("helm-pair/v1|" + id);

    private sealed record Plain(int V, string Token, string Keys);
}

/// <summary>A new device's side of a pairing: its key pair (memory only), the code to show, and the pickup secret.</summary>
public sealed class SyncPairingOffer : IDisposable
{
    private SyncPairingOffer(Uri server, string deviceName)
    {
        Server = server;
        DeviceName = deviceName;
        Key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = Key.ExportSubjectPublicKeyInfo();
        Code = SyncPairing.CodeOf(PublicKey);
        Id = SyncPairing.IdOf(Code);
        Pickup = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public static SyncPairingOffer Create(Uri server, string deviceName) => new(server, deviceName);

    public Uri Server { get; }
    public string DeviceName { get; }
    internal ECDiffieHellman Key { get; }
    public byte[] PublicKey { get; }

    /// <summary>18 digits; show it with <see cref="SyncPairing.FormatCode"/>.</summary>
    public string Code { get; }

    public string Id { get; }

    /// <summary>Only this device knows it: the server hands the package to whoever sends it.</summary>
    internal string Pickup { get; }

    /// <summary>When the server forgets the pairing (set once offered).</summary>
    public DateTimeOffset ExpiresAt { get; internal set; }

    public void Dispose() => Key.Dispose();
}

/// <summary>A code a signed-in device looked up and checked: the new device's name and key, ready to approve.</summary>
public sealed record SyncPairingRequest(string Id, string Code, byte[] PublicKey, string DeviceName, DateTimeOffset ExpiresAt);
