using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Helm.Core.Sync;

/// <summary>A record in plaintext, as it goes into or comes out of a payload envelope.</summary>
internal sealed record SealedContent(int SchemaVersion, long UpdatedAtMs, string DeviceId, string? Body);

/// <summary>
/// A payload was sealed with a key epoch this device does not have: the account key was rotated elsewhere, and
/// this device must unlock again (passphrase) before it can go on syncing. Deliberately not a
/// <see cref="CryptographicException"/>, which means "unreadable, skip it".
/// </summary>
public sealed class SyncKeyEpochException(int epoch) : Exception($"Sync data uses key epoch {epoch}, which this device does not have.")
{
    public int Epoch { get; } = epoch;
}

/// <summary>
/// End-to-end encryption for sync payloads.
/// <list type="bullet">
/// <item>Each collection has its own key per epoch: HKDF-SHA256(master of that epoch, info "helm-sync/v1/collection:&lt;name&gt;").</item>
/// <item>Envelope v2 = [0x02][epoch, uint32 BE][12-byte nonce][16-byte tag][AES-256-GCM ciphertext], AAD
/// "helm-sync/v2|&lt;epoch&gt;|&lt;collection&gt;|&lt;id&gt;". Envelope v1 (Helm 0.5.0) = [0x01][nonce][tag][ciphertext], AAD
/// "helm-sync/v1|&lt;collection&gt;|&lt;id&gt;", always epoch 1. Either way the server cannot move a payload to another record.</item>
/// <item>Plaintext = {"s": schemaVersion, "t": updatedAtMs, "d": deviceId, "b": body JSON or null}; the server
/// only learns collection, id, version, seq, the deleted flag and the payload size.</item>
/// </list>
/// A server can still replay an older payload of the same record; it cannot forge or read one.
/// </summary>
public sealed class SyncKeyring : IDisposable
{
    public const int KeySize = 32;
    private const byte FormatV1 = 1;
    private const byte FormatV2 = 2;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderV1 = 1 + NonceSize + TagSize;
    private const int HeaderV2 = 1 + 4 + NonceSize + TagSize;

    private readonly SyncKeySet _set;
    private readonly Dictionary<(int, string), byte[]> _keys = new();

    public SyncKeyring(ReadOnlySpan<byte> masterKey) : this(SyncKeySet.Single(masterKey)) { }

    public SyncKeyring(SyncKeySet keys)
    {
        _set = new SyncKeySet(keys.CurrentEpoch, keys.Keys);
    }

    public static byte[] CreateMasterKey() => RandomNumberGenerator.GetBytes(KeySize);

    public int CurrentEpoch => _set.CurrentEpoch;

    internal byte[] Seal(string collection, string id, SealedContent content)
    {
        var epoch = _set.CurrentEpoch;
        var plaintext = WriteContent(content);
        var envelope = new byte[HeaderV2 + plaintext.Length];
        envelope[0] = FormatV2;
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(1, 4), (uint)epoch);
        var nonce = envelope.AsSpan(5, NonceSize);
        var tag = envelope.AsSpan(5 + NonceSize, TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var gcm = new AesGcm(KeyFor(epoch, collection), TagSize);
        gcm.Encrypt(nonce, plaintext, envelope.AsSpan(HeaderV2), tag, AadV2(epoch, collection, id));
        CryptographicOperations.ZeroMemory(plaintext);
        return envelope;
    }

    /// <exception cref="CryptographicException">Wrong key, tampered payload, or a payload of another record.</exception>
    /// <exception cref="SyncKeyEpochException">Sealed with a newer key epoch than this device has.</exception>
    internal SealedContent Open(string collection, string id, byte[] envelope)
    {
        int epoch, header;
        byte[] aad;
        if (envelope.Length >= HeaderV2 && envelope[0] == FormatV2)
        {
            epoch = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(envelope.AsSpan(1, 4)), int.MaxValue);
            header = 5;
            aad = AadV2(epoch, collection, id);
        }
        else if (envelope.Length >= HeaderV1 && envelope[0] == FormatV1)
        {
            epoch = 1;
            header = 1;
            aad = Encoding.UTF8.GetBytes($"helm-sync/v1|{collection}|{id}");
        }
        else
        {
            throw new CryptographicException("Unknown sync payload format.");
        }
        if (!_set.TryGet(epoch, out _))
        {
            if (epoch > _set.CurrentEpoch) throw new SyncKeyEpochException(epoch);
            throw new CryptographicException($"No key for sync epoch {epoch}.");
        }

        var dataStart = header + NonceSize + TagSize;
        var plaintext = new byte[envelope.Length - dataStart];
        try
        {
            using var gcm = new AesGcm(KeyFor(epoch, collection), TagSize);
            gcm.Decrypt(envelope.AsSpan(header, NonceSize), envelope.AsSpan(dataStart), envelope.AsSpan(header + NonceSize, TagSize),
                plaintext, aad);
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
        _set.Dispose();
    }

    private byte[] KeyFor(int epoch, string collection)
    {
        lock (_keys)
        {
            if (_keys.TryGetValue((epoch, collection), out var key)) return key;
            _set.TryGet(epoch, out var master);
            key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeySize, salt: [],
                info: Encoding.UTF8.GetBytes("helm-sync/v1/collection:" + collection));
            _keys[(epoch, collection)] = key;
            return key;
        }
    }

    private static byte[] AadV2(int epoch, string collection, string id) => Encoding.UTF8.GetBytes($"helm-sync/v2|{epoch}|{collection}|{id}");

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
