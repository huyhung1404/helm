using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Konscious.Security.Cryptography;

namespace Helm.Modules.Vault.Backup;

/// <summary>A string field of a KDBX entry. Protected values are hidden by the inner stream (passwords, secrets).</summary>
internal sealed record KdbxString(string Key, string Value, bool Protected);

internal sealed record KdbxEntry(
    Guid Uuid,
    IReadOnlyList<KdbxString> Strings,
    IReadOnlyList<(string Name, int BinaryIndex)> Binaries,
    IReadOnlyList<string> Tags,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    IReadOnlyList<KdbxEntry> History);

internal sealed record KdbxGroup(Guid Uuid, string Name, IReadOnlyList<KdbxEntry> Entries, IReadOnlyList<KdbxGroup> Groups);

/// <summary>Argon2id parameters of the exported file (KeePass stores the memory in bytes).</summary>
internal sealed record KdbxKdf(ulong MemoryBytes, ulong Iterations, uint Parallelism)
{
    public static KdbxKdf Default { get; } = new(64UL * 1024 * 1024, 3, 2);
}

/// <summary>
/// Writes a KeePass database, format KDBX 4.1, that KeePassXC, KeePass and KeePassDX open: Argon2id key derivation,
/// AES-256-CBC, the HMAC-SHA256 block stream, gzip, and ChaCha20 for protected values. Only the password key is used
/// (no key file). Written from the KeePass format documentation; checked against KeePassXC in the tests.
/// </summary>
internal static class KdbxWriter
{
    private static readonly byte[] AesCipher = Convert.FromHexString("31C1F2E6BF714350BE5805216AFC5AFF");
    private static readonly byte[] Argon2idKdf = Convert.FromHexString("9E298B1956DB4773B23DFC3EC6F0A1E6");

    public static void Write(Stream output, string password, string databaseName, KdbxGroup root, IReadOnlyList<byte[]> binaries, KdbxKdf kdf)
    {
        var masterSeed = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);
        var salt = RandomNumberGenerator.GetBytes(32);
        var streamKey = RandomNumberGenerator.GetBytes(64);

        var header = OuterHeader(masterSeed, iv, salt, kdf);
        var transformed = TransformKey(password, salt, kdf);
        var encryptionKey = SHA256.HashData([.. masterSeed, .. transformed]);
        var hmacKey = SHA512.HashData([.. masterSeed, .. transformed, 1]);
        try
        {
            output.Write(header);
            output.Write(SHA256.HashData(header));
            output.Write(HMACSHA256.HashData(BlockKey(ulong.MaxValue, hmacKey), header));

            var payload = Encrypt(Compress(InnerHeader(streamKey, binaries), Xml(databaseName, root, streamKey)), encryptionKey, iv);
            WriteBlocks(output, payload, hmacKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transformed);
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(hmacKey);
            CryptographicOperations.ZeroMemory(streamKey);
        }
    }

    private static byte[] OuterHeader(byte[] masterSeed, byte[] iv, byte[] salt, KdbxKdf kdf)
    {
        using var header = new MemoryStream();
        using var w = new BinaryWriter(header);
        w.Write(0x9AA2D903u);
        w.Write(0xB54BFB67u);
        w.Write((ushort)1); // minor
        w.Write((ushort)4); // major
        Field(w, 2, AesCipher);
        Field(w, 3, BitConverter.GetBytes(1u)); // gzip
        Field(w, 4, masterSeed);
        Field(w, 7, iv);
        Field(w, 11, KdfParameters(salt, kdf));
        Field(w, 0, "\r\n\r\n"u8.ToArray());
        w.Flush();
        return header.ToArray();
    }

    private static byte[] KdfParameters(byte[] salt, KdbxKdf kdf)
    {
        using var dictionary = new MemoryStream();
        using var w = new BinaryWriter(dictionary);
        w.Write((ushort)0x0100);
        Variant(w, 0x42, "$UUID", Argon2idKdf);
        Variant(w, 0x42, "S", salt);
        Variant(w, 0x04, "P", BitConverter.GetBytes(kdf.Parallelism));
        Variant(w, 0x05, "M", BitConverter.GetBytes(kdf.MemoryBytes));
        Variant(w, 0x05, "I", BitConverter.GetBytes(kdf.Iterations));
        Variant(w, 0x04, "V", BitConverter.GetBytes(0x13u));
        w.Write((byte)0);
        w.Flush();
        return dictionary.ToArray();
    }

    /// <summary>Composite key (SHA-256 of SHA-256 of the password) through Argon2id.</summary>
    private static byte[] TransformKey(string password, byte[] salt, KdbxKdf kdf)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var composite = SHA256.HashData(SHA256.HashData(passwordBytes));
        CryptographicOperations.ZeroMemory(passwordBytes);
        try
        {
            using var argon = new Argon2id(composite)
            {
                Salt = salt,
                MemorySize = checked((int)(kdf.MemoryBytes / 1024)),
                Iterations = checked((int)kdf.Iterations),
                DegreeOfParallelism = checked((int)kdf.Parallelism),
            };
            return argon.GetBytes(32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(composite);
        }
    }

    private static byte[] InnerHeader(byte[] streamKey, IReadOnlyList<byte[]> binaries)
    {
        using var inner = new MemoryStream();
        using var w = new BinaryWriter(inner);
        Field(w, 1, BitConverter.GetBytes(3)); // ChaCha20
        Field(w, 2, streamKey);
        foreach (var binary in binaries) Field(w, 3, [0, .. binary]); // flags 0: not memory-protected
        Field(w, 0, []);
        w.Flush();
        return inner.ToArray();
    }

    private static byte[] Xml(string databaseName, KdbxGroup root, byte[] streamKey)
    {
        var hash = SHA512.HashData(streamKey);
        var stream = new ChaCha20(hash.AsSpan(0, 32), hash.AsSpan(32, 12));
        using var buffer = new MemoryStream();
        using (var x = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            x.WriteStartDocument(standalone: true);
            x.WriteStartElement("KeePassFile");
            x.WriteStartElement("Meta");
            x.WriteElementString("Generator", "Helm");
            x.WriteElementString("DatabaseName", databaseName);
            x.WriteElementString("DatabaseNameChanged", Time(DateTimeOffset.UtcNow));
            x.WriteStartElement("MemoryProtection");
            x.WriteElementString("ProtectTitle", "False");
            x.WriteElementString("ProtectUserName", "False");
            x.WriteElementString("ProtectPassword", "True");
            x.WriteElementString("ProtectURL", "False");
            x.WriteElementString("ProtectNotes", "False");
            x.WriteEndElement();
            x.WriteElementString("RecycleBinEnabled", "False");
            x.WriteEndElement();
            x.WriteStartElement("Root");
            WriteGroup(x, root, stream);
            x.WriteStartElement("DeletedObjects");
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndDocument();
        }
        return buffer.ToArray();
    }

    // Protected values are XORed with one continuous stream in document order, which is the order a reader meets them.
    private static void WriteGroup(XmlWriter x, KdbxGroup group, ChaCha20 stream)
    {
        x.WriteStartElement("Group");
        x.WriteElementString("UUID", Convert.ToBase64String(group.Uuid.ToByteArray()));
        x.WriteElementString("Name", group.Name);
        foreach (var entry in group.Entries) WriteEntry(x, entry, stream, inHistory: false);
        foreach (var child in group.Groups) WriteGroup(x, child, stream);
        x.WriteEndElement();
    }

    private static void WriteEntry(XmlWriter x, KdbxEntry entry, ChaCha20 stream, bool inHistory)
    {
        x.WriteStartElement("Entry");
        x.WriteElementString("UUID", Convert.ToBase64String(entry.Uuid.ToByteArray()));
        if (entry.Tags.Count > 0) x.WriteElementString("Tags", string.Join(';', entry.Tags));
        x.WriteStartElement("Times");
        x.WriteElementString("CreationTime", Time(entry.Created));
        x.WriteElementString("LastModificationTime", Time(entry.Modified));
        x.WriteElementString("LastAccessTime", Time(entry.Modified));
        x.WriteElementString("ExpiryTime", Time(entry.Modified));
        x.WriteElementString("Expires", "False");
        x.WriteElementString("UsageCount", "0");
        x.WriteElementString("LocationChanged", Time(entry.Modified));
        x.WriteEndElement();
        foreach (var field in entry.Strings)
        {
            x.WriteStartElement("String");
            x.WriteElementString("Key", field.Key);
            x.WriteStartElement("Value");
            if (field.Protected)
            {
                var bytes = Encoding.UTF8.GetBytes(field.Value);
                stream.Apply(bytes);
                x.WriteAttributeString("Protected", "True");
                x.WriteString(Convert.ToBase64String(bytes));
            }
            else
            {
                x.WriteString(field.Value);
            }
            x.WriteEndElement();
            x.WriteEndElement();
        }
        foreach (var (name, index) in entry.Binaries)
        {
            x.WriteStartElement("Binary");
            x.WriteElementString("Key", name);
            x.WriteStartElement("Value");
            x.WriteAttributeString("Ref", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            x.WriteEndElement();
            x.WriteEndElement();
        }
        if (!inHistory && entry.History.Count > 0)
        {
            x.WriteStartElement("History");
            foreach (var old in entry.History) WriteEntry(x, old with { Uuid = entry.Uuid }, stream, inHistory: true);
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    /// <summary>KDBX 4 times: seconds since 0001-01-01 UTC, little-endian int64, base64.</summary>
    private static string Time(DateTimeOffset at) =>
        Convert.ToBase64String(BitConverter.GetBytes((long)(at.UtcDateTime - DateTime.MinValue).TotalSeconds));

    private static byte[] Compress(byte[] innerHeader, byte[] xml)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(innerHeader);
            gzip.Write(xml);
        }
        return buffer.ToArray();
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
    }

    private static void WriteBlocks(Stream output, byte[] payload, byte[] hmacKey)
    {
        const int blockSize = 1024 * 1024;
        ulong index = 0;
        var prefix = new byte[12];
        for (var offset = 0; ; offset += blockSize, index++)
        {
            var length = Math.Max(0, Math.Min(blockSize, payload.Length - offset));
            var data = payload.AsSpan(Math.Min(offset, payload.Length), length).ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(prefix, index);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), data.Length);
            // HMAC over block index, size and data, with a key per block (so blocks cannot be reordered).
            output.Write(HMACSHA256.HashData(BlockKey(index, hmacKey), (byte[])[.. prefix, .. data]));
            output.Write(prefix, 8, 4);
            output.Write(data);
            if (data.Length == 0) break;
        }
    }

    private static byte[] BlockKey(ulong index, byte[] hmacKey)
    {
        Span<byte> prefix = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(prefix, index);
        return SHA512.HashData([.. prefix, .. hmacKey]);
    }

    private static void Field(BinaryWriter w, byte id, byte[] data)
    {
        w.Write(id);
        w.Write(data.Length);
        w.Write(data);
    }

    private static void Variant(BinaryWriter w, byte type, string name, byte[] value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        w.Write(type);
        w.Write(nameBytes.Length);
        w.Write(nameBytes);
        w.Write(value.Length);
        w.Write(value);
    }
}
