using System.Text;
using System.Text.RegularExpressions;

namespace Helm.Core.Sync;

/// <summary>
/// Helm sync tokens, modelled on GitHub personal access tokens:
/// <c>helm_pat_&lt;accountId:16&gt;_&lt;secret:40&gt;&lt;checksum:6&gt;</c> (base62). The checksum is CRC32 of everything
/// before it, so a mistyped or truncated token is caught before any request. Keep in sync with
/// server/sync-worker/src/token.ts.
/// </summary>
public static partial class SyncToken
{
    public const string Prefix = "helm_pat_";
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int ChecksumLength = 6;

    /// <summary>True when <paramref name="token"/> is well formed and its checksum matches.</summary>
    public static bool TryParse(string? token, out string accountId)
    {
        accountId = "";
        if (token is null) return false;
        var match = TokenPattern().Match(token);
        if (!match.Success) return false;
        if (Checksum(token[..^ChecksumLength]) != match.Groups[3].Value) return false;
        accountId = match.Groups[1].Value;
        return true;
    }

    public const string InvitePrefix = "helm_inv_";

    /// <summary>True when <paramref name="code"/> is a well-formed invite code with a matching checksum.</summary>
    public static bool IsInviteCode(string? code)
    {
        if (code is null) return false;
        var match = InvitePattern().Match(code);
        return match.Success && Checksum(code[..^ChecksumLength]) == match.Groups[2].Value;
    }

    /// <summary>First and last characters only, for logs and the UI ("helm_pat_Ab…xY9").</summary>
    public static string Redact(string token) =>
        token.Length <= Prefix.Length + 6 ? Prefix + "…" : $"{token[..(Prefix.Length + 2)]}…{token[^3..]}";

    internal static string Checksum(string text)
    {
        var value = Crc32(Encoding.UTF8.GetBytes(text));
        Span<char> chars = stackalloc char[ChecksumLength];
        for (var i = ChecksumLength - 1; i >= 0; i--)
        {
            chars[i] = Base62[(int)(value % 62)];
            value /= 62;
        }
        return new string(chars);
    }

    /// <summary>CRC-32/ISO-HDLC (zlib, PNG).</summary>
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    [GeneratedRegex("^helm_pat_([0-9A-Za-z]{16})_([0-9A-Za-z]{40})([0-9A-Za-z]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^helm_inv_([0-9A-Za-z]{40})([0-9A-Za-z]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex InvitePattern();
}
