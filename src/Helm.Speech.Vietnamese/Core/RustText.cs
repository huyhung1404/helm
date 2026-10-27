// Port support for sea-g2p (Rust): the Rust standard library's text semantics, in C#.
using System.Globalization;
using System.Text;

namespace Helm.Speech.Vietnamese.Core;

/// <summary>
/// The pieces of Rust's <c>str</c> and <c>char</c> behaviour the port leans on. Rust works on Unicode scalar values and
/// its character classes follow the Unicode derived properties (Alphabetic, Uppercase, White_Space), while .NET works on
/// UTF-16 code units and the general categories. Every rule of sea-g2p that asks "is this a letter?" or "how long is
/// this?" goes through here, so the answers match the original byte for byte.
/// </summary>
internal static class RustText
{
    /// <summary>Byte length of the UTF-8 encoding: what Rust's <c>str::len()</c> returns.</summary>
    public static int Utf8Len(string s)
    {
        var ascii = true;
        foreach (var c in s)
            if (c >= 0x80) { ascii = false; break; }
        return ascii ? s.Length : Encoding.UTF8.GetByteCount(s);
    }

    /// <summary>Number of Unicode scalar values: Rust's <c>chars().count()</c>.</summary>
    public static int CharCount(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }

    public static bool IsAscii(string s)
    {
        foreach (var c in s)
            if (c >= 0x80) return false;
        return true;
    }

    /// <summary>The scalar values of a string, one string each (a surrogate pair stays together).</summary>
    public static List<string> Chars(string s)
    {
        var list = new List<string>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                list.Add(s.Substring(i, 2));
                i++;
            }
            else
            {
                list.Add(s[i].ToString());
            }
        }
        return list;
    }

    /// <summary>The scalar values of a string as code points.</summary>
    public static int[] CodePoints(string s)
    {
        var list = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                list.Add(char.ConvertToUtf32(s[i], s[i + 1]));
                i++;
            }
            else
            {
                list.Add(s[i]);
            }
        }
        return list.ToArray();
    }

    public static string FromCodePoint(int cp) => cp < 0x10000 ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);

    private static UnicodeCategory Category(int cp) =>
        cp < 0x10000 ? CharUnicodeInfo.GetUnicodeCategory((char)cp) : CharUnicodeInfo.GetUnicodeCategory(cp);

    /// <summary>Rust <c>char::is_alphabetic</c>: letters, letter numbers and the combining vowel signs Unicode marks Alphabetic.</summary>
    public static bool IsAlphabetic(int cp)
    {
        if (cp < 0x80) return (cp | 0x20) is >= 'a' and <= 'z';
        switch (Category(cp))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.SpacingCombiningMark:
                return true;
            case UnicodeCategory.NonSpacingMark:
                return IsOtherAlphabeticMark(cp);
            case UnicodeCategory.OtherSymbol:
                return cp is >= 0x24B6 and <= 0x24E9 || cp is >= 0x1F130 and <= 0x1F149 || cp is >= 0x1F150 and <= 0x1F169
                    || cp is >= 0x1F170 and <= 0x1F189;
            default:
                return false;
        }
    }

    // The non-spacing marks with Other_Alphabetic: vowel signs of the Brahmic, Arabic and Hebrew scripts, plus the
    // Greek iota subscript. Diacritics such as U+0300-U+036F are deliberately not in it.
    private static bool IsOtherAlphabeticMark(int cp) =>
        cp == 0x0345 || cp is >= 0x05B0 and <= 0x05BD || cp == 0x05BF || cp is >= 0x05C1 and <= 0x05C2
        || cp is >= 0x05C4 and <= 0x05C5 || cp == 0x05C7 || cp is >= 0x0610 and <= 0x061A || cp is >= 0x064B and <= 0x0657
        || cp is >= 0x0659 and <= 0x065F || cp == 0x0670 || cp is >= 0x06D6 and <= 0x06DC || cp is >= 0x06E1 and <= 0x06E4
        || cp is >= 0x06E7 and <= 0x06E8 || cp == 0x06ED || cp is >= 0x0900 and <= 0x0DFF && !IsBrahmicNonAlphabetic(cp)
        || cp == 0x0E31 || cp is >= 0x0E34 and <= 0x0E3A || cp == 0x0E4D || cp == 0x0EB1 || cp is >= 0x0EB4 and <= 0x0EB9
        || cp is >= 0x0EBB and <= 0x0EBC || cp == 0x0ECD || cp is >= 0x1000 and <= 0x109F;

    // Within the Indic blocks the nukta, virama and stress marks are not Alphabetic; the vowel signs are.
    private static bool IsBrahmicNonAlphabetic(int cp)
    {
        var low = cp & 0x7F;
        return low is 0x3C or 0x4D;
    }

    /// <summary>Rust <c>char::is_numeric</c>: the N categories.</summary>
    public static bool IsNumeric(int cp)
    {
        if (cp < 0x80) return cp is >= '0' and <= '9';
        var c = Category(cp);
        return c is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    public static bool IsAlphanumeric(int cp) => IsAlphabetic(cp) || IsNumeric(cp);

    /// <summary>Rust <c>char::is_whitespace</c> (the White_Space property), which .NET's <c>IsWhiteSpace</c> also follows.</summary>
    public static bool IsWhitespace(int cp) => cp < 0x10000 && char.IsWhiteSpace((char)cp);

    public static bool IsControl(int cp) => cp < 0x10000 && Category(cp) == UnicodeCategory.Control;

    /// <summary>Rust <c>char::is_uppercase</c>: Lu plus Other_Uppercase (Roman numerals, circled capitals).</summary>
    public static bool IsUppercase(int cp)
    {
        if (cp < 0x80) return cp is >= 'A' and <= 'Z';
        if (Category(cp) == UnicodeCategory.UppercaseLetter) return true;
        return cp is >= 0x2160 and <= 0x216F || cp is >= 0x24B6 and <= 0x24CF || cp is >= 0x1F130 and <= 0x1F149
            || cp is >= 0x1F150 and <= 0x1F169 || cp is >= 0x1F170 and <= 0x1F189;
    }

    /// <summary>Rust <c>char::is_lowercase</c>: Ll plus Other_Lowercase (ª º, modifier and sub/superscript letters…).</summary>
    public static bool IsLowercase(int cp)
    {
        if (cp < 0x80) return cp is >= 'a' and <= 'z';
        if (Category(cp) == UnicodeCategory.LowercaseLetter) return true;
        return cp is 0x00AA or 0x00BA or 0x0345 or 0x037A or 0x10FC or 0x1D78 or 0x2071 or 0x207F or 0xA770
            || cp is >= 0x02B0 and <= 0x02B8 || cp is >= 0x02C0 and <= 0x02C1 || cp is >= 0x02E0 and <= 0x02E4
            || cp is >= 0x1D2C and <= 0x1D6A || cp is >= 0x1D9B and <= 0x1DBF || cp is >= 0x2090 and <= 0x209C
            || cp is >= 0x2170 and <= 0x217F || cp is >= 0x24D0 and <= 0x24E9 || cp is >= 0x2C7C and <= 0x2C7D
            || cp is >= 0xA69C and <= 0xA69D || cp is >= 0xA7F8 and <= 0xA7F9 || cp is >= 0xAB5C and <= 0xAB5F;
    }

    public static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
    public static bool IsAsciiAlpha(char c) => (c | 0x20) is >= 'a' and <= 'z';
    public static bool IsAsciiAlnum(char c) => IsAsciiDigit(c) || IsAsciiAlpha(c);
    public static bool IsAsciiUpper(char c) => c is >= 'A' and <= 'Z';
    public static bool IsAsciiLower(char c) => c is >= 'a' and <= 'z';

    public static bool Any(string s, Func<int, bool> predicate)
    {
        for (var i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            if (predicate(cp)) return true;
        }
        return false;
    }

    public static bool All(string s, Func<int, bool> predicate) => !Any(s, cp => !predicate(cp));

    /// <summary>Lowercase mapping of one scalar value, as Rust's <c>char::to_lowercase().to_string()</c> gives it.</summary>
    public static string LowerChar(int cp)
    {
        if (cp < 0x80) return ((char)(cp is >= 'A' and <= 'Z' ? cp + 32 : cp)).ToString();
        if (cp == 0x0130) return "i̇";
        if (cp < 0x10000) return char.ToLowerInvariant((char)cp).ToString();
        return Rune.ToLowerInvariant(new Rune(cp)).ToString();
    }

    /// <summary>Rust <c>str::to_lowercase</c>: full lowercase mapping, including the final-sigma rule.</summary>
    public static string ToLower(string s)
    {
        var ascii = true;
        foreach (var c in s)
            if (c >= 0x80) { ascii = false; break; }
        if (ascii) return s.ToLowerInvariant();
        var cps = CodePoints(s);
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < cps.Length; i++)
        {
            var cp = cps[i];
            if (cp == 0x03A3)
            {
                sb.Append(IsFinalSigma(cps, i) ? 'ς' : 'σ');
                continue;
            }
            sb.Append(LowerChar(cp));
        }
        return sb.ToString();
    }

    private static bool IsFinalSigma(int[] cps, int i)
    {
        var before = false;
        for (var j = i - 1; j >= 0; j--)
        {
            if (IsCaseIgnorable(cps[j])) continue;
            before = IsCased(cps[j]);
            break;
        }
        if (!before) return false;
        for (var j = i + 1; j < cps.Length; j++)
        {
            if (IsCaseIgnorable(cps[j])) continue;
            return !IsCased(cps[j]);
        }
        return true;
    }

    private static bool IsCased(int cp) =>
        IsUppercase(cp) || IsLowercase(cp) || Category(cp) == UnicodeCategory.TitlecaseLetter;

    private static bool IsCaseIgnorable(int cp)
    {
        if (cp is '\'' or '.' or ':' or '^' or '`' or 0x00B7 or 0x2018 or 0x2019 or 0x2024 or 0x2027) return true;
        var c = Category(cp);
        return c is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
            or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol;
    }

    /// <summary>Rust <c>str::to_uppercase</c>: full uppercase mapping (ß becomes SS).</summary>
    public static string ToUpper(string s)
    {
        var ascii = true;
        foreach (var c in s)
            if (c >= 0x80) { ascii = false; break; }
        if (ascii) return s.ToUpperInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var cp in CodePoints(s))
        {
            switch (cp)
            {
                case 0x00DF: sb.Append("SS"); break;
                case 0xFB00: sb.Append("FF"); break;
                case 0xFB01: sb.Append("FI"); break;
                case 0xFB02: sb.Append("FL"); break;
                case 0xFB03: sb.Append("FFI"); break;
                case 0xFB04: sb.Append("FFL"); break;
                case 0xFB05:
                case 0xFB06: sb.Append("ST"); break;
                case 0x0149: sb.Append("ʼN"); break;
                default:
                    if (cp < 0x10000) sb.Append(char.ToUpperInvariant((char)cp));
                    else sb.Append(Rune.ToUpperInvariant(new Rune(cp)).ToString());
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Rust <c>str::split_whitespace</c>.</summary>
    public static string[] SplitWhitespace(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Rust <c>str::trim_matches</c> with a predicate.</summary>
    public static string TrimMatches(string s, Func<int, bool> strip) => TrimEndMatches(TrimStartMatches(s, strip), strip);

    public static string TrimStartMatches(string s, Func<int, bool> strip)
    {
        var i = 0;
        while (i < s.Length)
        {
            int cp = s[i];
            var width = 1;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                width = 2;
            }
            if (!strip(cp)) break;
            i += width;
        }
        return i == 0 ? s : s[i..];
    }

    public static string TrimEndMatches(string s, Func<int, bool> strip)
    {
        var end = s.Length;
        while (end > 0)
        {
            int cp = s[end - 1];
            var width = 1;
            if (char.IsLowSurrogate(s[end - 1]) && end >= 2 && char.IsHighSurrogate(s[end - 2]))
            {
                cp = char.ConvertToUtf32(s[end - 2], s[end - 1]);
                width = 2;
            }
            if (!strip(cp)) break;
            end -= width;
        }
        return end == s.Length ? s : s[..end];
    }

    /// <summary>The last code point of a non-empty string.</summary>
    public static int LastChar(string s)
    {
        var n = s.Length;
        if (n >= 2 && char.IsLowSurrogate(s[n - 1]) && char.IsHighSurrogate(s[n - 2]))
            return char.ConvertToUtf32(s[n - 2], s[n - 1]);
        return s[n - 1];
    }

    /// <summary>The first code point of a non-empty string.</summary>
    public static int FirstChar(string s)
    {
        if (s.Length >= 2 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1]))
            return char.ConvertToUtf32(s[0], s[1]);
        return s[0];
    }

    /// <summary>Rust <c>str::parse::&lt;i64&gt;</c> restricted to the range of the target: an optional sign, then ASCII digits.</summary>
    public static bool TryParseInt(string s, long min, long max, out long value)
    {
        value = 0;
        if (s.Length == 0) return false;
        var i = 0;
        var negative = false;
        if (s[0] is '+' or '-')
        {
            negative = s[0] == '-';
            i = 1;
            if (s.Length == 1) return false;
        }
        decimal acc = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c is < '0' or > '9') return false;
            acc = acc * 10 + (c - '0');
            if (acc > (decimal)max + 1) return false;
        }
        if (negative) acc = -acc;
        if (acc < min || acc > max) return false;
        value = (long)acc;
        return true;
    }

    /// <summary><c>s.parse::&lt;i32&gt;().unwrap_or(fallback)</c>.</summary>
    public static int ParseI32(string s, int fallback) =>
        TryParseInt(s, int.MinValue, int.MaxValue, out var v) ? (int)v : fallback;

    /// <summary><c>s.parse::&lt;u64&gt;()</c>; Rust's u64 also rejects a minus sign.</summary>
    public static bool TryParseU64(string s, out ulong value)
    {
        value = 0;
        if (s.Length == 0) return false;
        var i = s[0] == '+' ? 1 : 0;
        if (i == s.Length) return false;
        ulong acc = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c is < '0' or > '9') return false;
            var d = (ulong)(c - '0');
            if (acc > (ulong.MaxValue - d) / 10) return false;
            acc = acc * 10 + d;
        }
        value = acc;
        return true;
    }

    /// <summary>Rust <c>str::replace(from, to)</c> (ordinal).</summary>
    public static string Replace(string s, string from, string to) => s.Replace(from, to, StringComparison.Ordinal);
}
