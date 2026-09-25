using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Helm.Core.Sync;

/// <summary>A record in plaintext, as it goes into or comes out of a payload envelope.</summary>
internal sealed record SealedContent(int SchemaVersion, long UpdatedAtMs, string DeviceId, string? Body);

/// <summary>
/// End-to-end encryption for sync payloads.
/// <list type="bullet">
/// <item>Each collection has its own key, HKDF-SHA256(master, info "helm-sync/v1/collection:&lt;name&gt;").</item>
/// <item>Envelope = [0x01][12-byte nonce][16-byte tag][AES-256-GCM ciphertext].</item>
/// <item>AAD = "helm-sync/v1|&lt;collection&gt;|&lt;id&gt;", so the server cannot move a payload to another record.</item>
/// <item>Plaintext = {"s": schemaVersion, "t": updatedAtMs, "d": deviceId, "b": body JSON or null}; the server
/// only learns collection, id, version, seq, the deleted flag and the payload size.</item>
/// </list>
/// A server can still replay an older payload of the same record; it cannot forge or read one.
/// </summary>
public sealed class SyncKeyring : IDisposable
{
    public const int KeySize = 32;
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    private readonly byte[] _master;
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public SyncKeyring(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != KeySize) throw new ArgumentException($"The master key must be {KeySize} bytes.", nameof(masterKey));
        _master = masterKey.ToArray();
    }

    public static byte[] CreateMasterKey() => RandomNumberGenerator.GetBytes(KeySize);

    internal byte[] Seal(string collection, string id, SealedContent content)
    {
        var plaintext = WriteContent(content);
        var envelope = new byte[HeaderSize + plaintext.Length];
        var nonce = envelope.AsSpan(1, NonceSize);
        var tag = envelope.AsSpan(1 + NonceSize, TagSize);
        envelope[0] = FormatVersion;
        RandomNumberGenerator.Fill(nonce);

        using var gcm = new AesGcm(KeyFor(collection), TagSize);
        gcm.Encrypt(nonce, plaintext, envelope.AsSpan(HeaderSize), tag, Aad(collection, id));
        CryptographicOperations.ZeroMemory(plaintext);
        return envelope;
    }

    /// <exception cref="CryptographicException">Wrong key, tampered payload, or a payload of another record.</exception>
    internal SealedContent Open(string collection, string id, byte[] envelope)
    {
        if (envelope.Length < HeaderSize || envelope[0] != FormatVersion)
            throw new CryptographicException("Unknown sync payload format.");

        var plaintext = new byte[envelope.Length - HeaderSize];
        try
        {
            using var gcm = new AesGcm(KeyFor(collection), TagSize);
            gcm.Decrypt(envelope.AsSpan(1, NonceSize), envelope.AsSpan(HeaderSize), envelope.AsSpan(1 + NonceSize, TagSize),
                plaintext, Aad(collection, id));
            return ReadContent(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose()
    {
        lock (_keys)
        {
            foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
            _keys.Clear();
        }
        CryptographicOperations.ZeroMemory(_master);
    }

    private byte[] KeyFor(string collection)
    {
        lock (_keys)
        {
            if (_keys.TryGetValue(collection, out var key)) return key;
            key = HKDF.DeriveKey(HashAlgorithmName.SHA256, _master, KeySize, salt: [],
                info: Encoding.UTF8.GetBytes("helm-sync/v1/collection:" + collection));
            _keys[collection] = key;
            return key;
        }
    }

    private static byte[] Aad(string collection, string id) => Encoding.UTF8.GetBytes($"helm-sync/v1|{collection}|{id}");

    private static byte[] WriteContent(SealedContent content)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("s", content.SchemaVersion);
            writer.WriteNumber("t", content.UpdatedAtMs);
            writer.WriteString("d", content.DeviceId);
            if (content.Body is null) writer.WriteNull("b");
            else writer.WriteString("b", content.Body);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static SealedContent ReadContent(byte[] plaintext)
    {
        try
        {
            using var doc = JsonDocument.Parse(plaintext);
            var root = doc.RootElement;
            var body = root.GetProperty("b");
            return new SealedContent(
                root.GetProperty("s").GetInt32(),
                root.GetProperty("t").GetInt64(),
                root.GetProperty("d").GetString() ?? "",
                body.ValueKind == JsonValueKind.Null ? null : body.GetString());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new CryptographicException("Sync payload decrypted but its content is malformed.", ex);
        }
    }
}
