// Port of sea-g2p src/lang/vi/numerical.rs.
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Bare numbers left over once every specialised pass (dates, times, phones, units, codes) has taken its share. What
/// remains is plain quantities, so the rules are simple: ordinals, mobile numbers, then every remaining digit run.
/// </summary>
internal static partial class Numerical
{
    [GeneratedRegex(@"(?<!\d)([-–—])?(\d+(?:,\d+|(?:\.\d{3})+(?!\d)|\.\d+|(?:\s\d{3})+(?!\d))?)(?!\d)", Rx.Options)]
    private static partial Regex ReNumber();

    /// <summary>A number with optional unit followed by one or more "x number" factors: "10x20x30", "5m x 3m".</summary>
    [GeneratedRegex(@"(?i)\d+(?:\s*[a-zA-Zμµ²³°]+\d*)?(?:\s*[x×]\s*\d+(?:\s*[a-zA-Zμµ²³°]+\d*)?)+", Rx.Options)]
    internal static partial Regex ReMultiply();

    [GeneratedRegex(@"(?i)(?<=\d|[a-zA-Zμµ²³°])\s*[x×]\s*(?=\d)", Rx.Options)]
    private static partial Regex ReExpandMultiply();

    [GeneratedRegex(@"(?i)" + @"(thứ|hạng)(\s+)(\d+)" + Rx.BR, Rx.Options)]
    private static partial Regex ReOrdinal();

    [GeneratedRegex(@"((\+84|84|0|0084)(3|5|7|8|9)[0-9]{8})", Rx.Options)]
    private static partial Regex RePhone();

    [GeneratedRegex(@"\d+(\.\d{3})+", Rx.Options)]
    private static partial Regex ReDotSep();

    private static string NormalizeDotSep(string number)
    {
        var m = ReDotSep().Match(number);
        if (m.Success && number.Contains('.') && m.Value == number) return number.Replace(".", "", StringComparison.Ordinal);
        return number;
    }

    /// <summary>The last byte of the UTF-8 encoding of the character that ends just before <paramref name="index"/>.</summary>
    internal static int LastUtf8Byte(string s, int index)
    {
        int cp = s[index - 1];
        if (char.IsLowSurrogate(s[index - 1]) && index >= 2 && char.IsHighSurrogate(s[index - 2]))
            cp = char.ConvertToUtf32(s[index - 2], s[index - 1]);
        return cp < 0x80 ? cp : 0x80 | (cp & 0x3F);
    }

    public static string NumToWords(string number, bool negative)
    {
        if (!number.Contains('.') && !number.Contains(',') && !number.Contains(' ') && number.StartsWith('0')
            && RustText.Utf8Len(number) > 1)
        {
            return ((negative ? "âm " : "") + Num2Vi.N2WSingle(number)).Trim();
        }

        if (number.Contains('.'))
        {
            var m = ReDotSep().Match(number);
            var isThousands = m.Success && m.Value == number;
            if (!isThousands)
            {
                var parts = number.Split('.');
                if (parts.Length == 2)
                    return ((negative ? "âm " : "") + Num2Vi.N2W(parts[0]) + " chấm " + Num2Vi.N2WDecimal(parts[1])).Trim();
            }
        }

        var noDots = NormalizeDotSep(number).Replace(" ", "", StringComparison.Ordinal);
        if (noDots.Contains(','))
        {
            var parts = noDots.Split(',');
            return ((negative ? "âm " : "") + Num2Vi.N2W(parts[0]) + " phẩy " + Num2Vi.N2WDecimal(parts[1])).Trim();
        }
        if (negative) return ("âm " + Num2Vi.N2W(noDots)).Trim();
        return Num2Vi.N2W(noDots);
    }

    public static string ExpandMultiplyNumber(string text) => ReExpandMultiply().Replace(text, " nhân ");

    public static string NormalizeNumberVi(string text)
    {
        var result = ReOrdinal().Replace(text, m =>
        {
            var prefix = m.V(1);
            var space = m.V(2);
            var number = m.V(3);
            if (number == "1") return prefix + space + "nhất";
            if (number == "4") return prefix + space + "tư";
            return prefix + space + Num2Vi.N2W(number);
        });

        result = RePhone().Replace(result, m => Num2Vi.N2WSingle(m.Value));

        var source = result;
        return ReNumber().Replace(source, m =>
        {
            var start = m.Index;
            var neg = m.G(1);
            var numberStr = m.V(2);
            var isNeg = false;
            if (neg != null)
            {
                // sea-g2p reads the UTF-8 *byte* before the match as a char, so the last byte of a multi-byte letter
                // (0x85 or 0xA0, e.g. "Ạ") counts as whitespace there. Reproduced on purpose.
                if (start == 0)
                {
                    isNeg = true;
                }
                else
                {
                    var prefix = (char)LastUtf8Byte(source, start);
                    isNeg = char.IsWhiteSpace(prefix) || "([;,. ".Contains(prefix);
                }
            }
            var word = NumToWords(numberStr, isNeg);
            if (neg != null && !isNeg) return neg + word;
            return " " + word + " ";
        });
    }
}
