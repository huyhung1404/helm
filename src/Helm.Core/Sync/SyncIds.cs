using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Helm.Core.Sync;

/// <summary>Identifiers for synced data: collection names, record ids and new ids.</summary>
public static partial class SyncIds
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int MaxIdLength = 128;

    /// <summary>
    /// A ULID: 48-bit Unix milliseconds + 80 random bits in Crockford base32 (26 chars). Ids created later sort
    /// after earlier ones (to the millisecond), which keeps append-only logs in order without a separate column.
    /// </summary>
    public static string NewId(TimeProvider? time = null)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)(time ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)ms;
            ms >>= 8;
        }
        RandomNumberGenerator.Fill(bytes[6..]);

        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(bytes);
        Span<char> chars = stackalloc char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Crockford[(int)(value & 31)];
            value >>= 5;
        }
        return new string(chars);
    }

    /// <summary>Lowercase, starts with a letter or digit, then letters, digits, '.', '_' or '-'; at most 64 chars.</summary>
    public static bool IsValidCollection(string? name) => name is not null && CollectionPattern().IsMatch(name);

    /// <summary>Any non-empty string up to <see cref="MaxIdLength"/> chars without control characters.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength && !id.Any(char.IsControl);

    internal static void EnsureCollection(string name)
    {
        if (!IsValidCollection(name)) throw new ArgumentException($"Invalid sync collection name '{name}'.", nameof(name));
    }

    internal static void EnsureId(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException($"Invalid sync record id '{id}'.", nameof(id));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionPattern();
}
