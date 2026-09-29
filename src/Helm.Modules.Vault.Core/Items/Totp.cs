using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Helm.Modules.Vault.Items;

public enum TotpAlgorithm
{
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>
/// A time-based one-time password (RFC 6238), as authenticator apps make them: the value of a
/// <see cref="VaultFieldKind.Totp"/> field is the secret, either the key the site shows ("JBSW Y3DP …", base32) or the
/// otpauth:// link its QR code holds. The code changes every <see cref="Period"/> seconds.
/// </summary>
public sealed record Totp(byte[] Secret, int Digits = 6, int Period = 30, TotpAlgorithm Algorithm = TotpAlgorithm.Sha1,
    string Issuer = "", string Account = "")
{
    /// <summary>Reads a base32 key or an otpauth://totp/ link; false with a reason for anything else.</summary>
    public static bool TryParse(string? value, out Totp? totp, out string error)
    {
        totp = null;
        error = "";
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "Paste the key the site shows (or the otpauth:// link of its QR code).";
            return false;
        }
        if (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)) return TryParseUri(text, out totp, out error);
        if (!TryBase32(text, out var secret) || secret.Length < 10)
        {
            error = "That is not a valid key: it uses the letters A–Z and the digits 2–7, usually 16 or 32 of them.";
            return false;
        }
        totp = new Totp(secret);
        return true;
    }

    public static Totp? Parse(string? value) => TryParse(value, out var totp, out _) ? totp : null;

    /// <summary>The code for <paramref name="now"/>, e.g. "492039".</summary>
    public string Code(DateTimeOffset now) => Code(now.ToUnixTimeSeconds() / Period);

    /// <summary>The code of time step <paramref name="counter"/> (RFC 4226 HOTP with the time as the counter).</summary>
    public string Code(long counter)
    {
        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);
        var hash = Algorithm switch
        {
            TotpAlgorithm.Sha256 => HMACSHA256.HashData(Secret, message),
            TotpAlgorithm.Sha512 => HMACSHA512.HashData(Secret, message),
            _ => HMACSHA1.HashData(Secret, message),
        };
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var code = binary % (int)Math.Pow(10, Digits);
        return code.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    /// <summary>Seconds until the code changes (1…Period).</summary>
    public int SecondsLeft(DateTimeOffset now) => Period - (int)(now.ToUnixTimeSeconds() % Period);

    /// <summary>"492 039": easier to read and type. Eight digits split 4 + 4.</summary>
    public static string Group(string code) => code.Length switch
    {
        6 => code[..3] + " " + code[3..],
        8 => code[..4] + " " + code[4..],
        _ => code,
    };

    private static bool TryParseUri(string text, out Totp? totp, out string error)
    {
        totp = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.Host.Equals("totp", StringComparison.OrdinalIgnoreCase))
        {
            error = uri?.Host.Equals("hotp", StringComparison.OrdinalIgnoreCase) == true
                ? "Counter-based codes (HOTP) are not supported; only time-based ones (TOTP)."
                : "That is not an otpauth://totp/ link.";
            return false;
        }
        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("secret", out var secretText) || !TryBase32(secretText, out var secret) || secret.Length < 10)
        {
            error = "The link has no valid secret.";
            return false;
        }
        var digits = query.TryGetValue("digits", out var d) && int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var dv) ? dv : 6;
        var period = query.TryGetValue("period", out var p) && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var pv) ? pv : 30;
        if (digits is < 6 or > 8 || period is < 1 or > 300)
        {
            error = "The link asks for codes Helm cannot make (6–8 digits, a period of 1–300 seconds).";
            return false;
        }
        var algorithm = (query.GetValueOrDefault("algorithm") ?? "SHA1").ToUpperInvariant() switch
        {
            "SHA1" => (TotpAlgorithm?)TotpAlgorithm.Sha1,
            "SHA256" => TotpAlgorithm.Sha256,
            "SHA512" => TotpAlgorithm.Sha512,
            _ => null,
        };
        if (algorithm is null)
        {
            error = "The link uses an unknown algorithm.";
            return false;
        }
        // The label is "Issuer:account" (either part may be missing); an issuer parameter wins.
        var label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        var colon = label.IndexOf(':');
        var issuer = query.GetValueOrDefault("issuer") ?? (colon > 0 ? label[..colon] : "");
        var account = colon >= 0 ? label[(colon + 1)..].Trim() : label;
        totp = new Totp(secret, digits, period, algorithm.Value, issuer.Trim(), account);
        error = "";
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            values[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return values;
    }

    /// <summary>RFC 4648 base32, forgiving spaces, dashes, lower case and missing padding (as sites print keys).</summary>
    internal static bool TryBase32(string text, out byte[] bytes)
    {
        bytes = [];
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is ' ' or '-' or '\t') continue;
            if (c == '=') break;
            clean.Append(char.ToUpperInvariant(c));
        }
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean.ToString())
        {
            var value = alphabet.IndexOf(c);
            if (value < 0) return false;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        bytes = output.ToArray();
        return bytes.Length > 0;
    }

    /// <summary>The otpauth:// link for this secret (what a QR code holds), e.g. for KeePass export.</summary>
    public static string ToUri(Totp totp, string fallbackLabel = "")
    {
        var issuer = totp.Issuer.Length > 0 ? totp.Issuer : fallbackLabel;
        var label = Uri.EscapeDataString(issuer.Length > 0 && totp.Account.Length > 0 ? $"{issuer}:{totp.Account}" : issuer.Length > 0 ? issuer : totp.Account);
        var query = $"secret={ToBase32(totp.Secret)}";
        if (issuer.Length > 0) query += "&issuer=" + Uri.EscapeDataString(issuer);
        if (totp.Algorithm != TotpAlgorithm.Sha1) query += "&algorithm=" + totp.Algorithm.ToString().ToUpperInvariant();
        if (totp.Digits != 6) query += "&digits=" + totp.Digits.ToString(CultureInfo.InvariantCulture);
        if (totp.Period != 30) query += "&period=" + totp.Period.ToString(CultureInfo.InvariantCulture);
        return $"otpauth://totp/{label}?{query}";
    }

    internal static string ToBase32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(buffer >> bits) & 31]);
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    /// <summary>Never prints the secret.</summary>
    public override string ToString() => $"Totp({Issuer}, {Digits} digits, {Period}s, ***)";
}
