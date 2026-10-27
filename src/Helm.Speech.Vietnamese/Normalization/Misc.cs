// Port of sea-g2p src/lang/vi/misc.rs (symbols, Roman numerals, letters, primes, temperatures, normalize_others).
using System.Text;
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Stage 15, the last chance for anything still unread: titles and address prefixes, Roman numerals (only after a cue
/// word such as "thế kỷ" or "chương"), letter names, primes, symbols, brackets, temperatures, acronyms, versions and
/// ratios. It ends with the whitelist that deletes every character no rule claimed.
/// </summary>
internal static partial class Misc
{
    [GeneratedRegex(Rx.BL + @"(?=[IVXLCDM]{2,})(?:M{0,4}(?:CM|CD|D?C{0,3})(?:XC|XL|L?X{0,3})(?:IX|IV|V?I{0,3}))(?<=[IVXLCDM])" + Rx.BR, Rx.Options)]
    private static partial Regex ReRomanNumber();

    /// <summary>A Roman section number opening a line ("I. VỀ …"); group 4 is the uppercase heading after it.</summary>
    [GeneratedRegex(@"(?m)^([ \t]*)([IVXLCDM]+)(\.[ \t]+)(?=(\p{Lu}+))", Rx.Options)]
    private static partial Regex ReRomanListMarker();

    private const int RomanMarkerMax = 30;

    [GeneratedRegex(Rx.BL + @"[IVX]" + Rx.BR + @"(?!['’])", Rx.Options)]
    private static partial Regex ReRomanSingle();

    [GeneratedRegex(Rx.BL + @"(TS|GS|BS|ThS|PGS|KS|ĐH)\.\s+(?=\p{Lu})", Rx.Options)]
    private static partial Regex ReTitleDot();

    [GeneratedRegex(Rx.BL + @"Q\.\s*(?=\d|\p{Lu}\p{Ll})", Rx.Options)]
    private static partial Regex ReDistrictDot();

    [GeneratedRegex(Rx.BL + @"P\.\s*(?=\d|\p{Lu}\p{Ll})", Rx.Options)]
    private static partial Regex ReWardDot();

    [GeneratedRegex(@"(?<!')" + Rx.BL + @"([a-zA-Z])(?!['’]" + Rx.W + @")" + Rx.BR + @"(\.?)", Rx.Options)]
    private static partial Regex ReStandaloneLetter();

    [GeneratedRegex(@"(?<![-–—])" + Rx.BL + @"(\d+(?:\.\d+){2,})" + Rx.BR, Rx.Options)]
    private static partial Regex ReVersion();

    [GeneratedRegex(@"(" + Rx.BL + @"[a-zA-Z0-9])(['’]+)(?!" + Rx.W + @")", Rx.Options)]
    private static partial Regex RePrime();

    [GeneratedRegex(@"\|(\S(?:[^|]*\S)?)\|", Rx.Options)]
    private static partial Regex ReAbs();

    [GeneratedRegex(@"(?<=\d)(['’]+|[\x22”])", Rx.Options)]
    private static partial Regex RePrimeDigit();

    [GeneratedRegex(@"(?i)" + @"(chữ|chữ cái|kí tự|ký tự)\s+(['""]?)([a-z])(['""]?)" + Rx.B, Rx.Options)]
    private static partial Regex ReLetter();

    [GeneratedRegex(Rx.BL + @"(\d+)([a-zA-Z])" + Rx.BR, Rx.Options)]
    private static partial Regex ReAlphanumeric();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"([a-z]{1,4})\s*&\s*([a-z]{1,4})" + Rx.BR, Rx.Options)]
    private static partial Regex ReAmpersandAcronym();

    [GeneratedRegex(Rx.BL + @"([a-zA-Z])(\d+)" + Rx.BR, Rx.Options)]
    private static partial Regex ReLetterDigit();

    [GeneratedRegex(@"[\(\[\{]\s*(.*?)\s*[\)\]\}]", Rx.Options)]
    private static partial Regex ReBrackets();

    [GeneratedRegex(@"[\[\]\(\)\{\}]", Rx.Options)]
    private static partial Regex ReStripBrackets();

    [GeneratedRegex(@"(?i)" + @"-(\d+(?:[.,]\d+)?)\s*°\s*c" + Rx.BR, Rx.Options)]
    private static partial Regex ReTempCNeg();

    [GeneratedRegex(@"(?i)" + @"-(\d+(?:[.,]\d+)?)\s*°\s*f" + Rx.BR, Rx.Options)]
    private static partial Regex ReTempFNeg();

    [GeneratedRegex(@"(?i)" + @"(\d+(?:[.,]\d+)?)\s*°\s*c" + Rx.BR, Rx.Options)]
    private static partial Regex ReTempC();

    [GeneratedRegex(@"(?i)" + @"(\d+(?:[.,]\d+)?)\s*°\s*f" + Rx.BR, Rx.Options)]
    private static partial Regex ReTempF();

    [GeneratedRegex(@"(?<![.,\d])" + Rx.BL + @"(\d+):(\d+(?:\.\d+)?)" + Rx.BR + @"(?!\d)(?![.,]\d)", Rx.Options)]
    private static partial Regex ReStandardColon();

    [GeneratedRegex(@"(?<![.,\d:])\d+(?::\d+){2,}(?![\d:])(?![.,]\d)", Rx.Options)]
    private static partial Regex ReRatioMulti();

    /// <summary>The final whitelist: ASCII letters and digits, whitespace, Vietnamese letters and . , ! ? _ ' -.</summary>
    private static readonly Regex ReCleanOthers =
        Rx.Build(@"[\uD800-\uDBFF][\uDC00-\uDFFF]|[^a-zA-Z0-9\s" + Resources.ViLetters + @".,!?_'\-]");

    [GeneratedRegex(@"[“”„]", Rx.Options)]
    private static partial Regex ReCleanQuotes();

    [GeneratedRegex(@"(^|[\s.,!?;:])[‘’']+|[‘’']+(\z|[\s.,!?;:])", Rx.Options)]
    private static partial Regex ReCleanQuotesEdges();

    [GeneratedRegex(@"[:;]", Rx.Options)]
    private static partial Regex ReColonSemicolon();

    [GeneratedRegex(Rx.BL + @"([a-zA-Z]+)\^([-+]?\d+)" + Rx.BR, Rx.Options)]
    private static partial Regex ReUnitPowers();

    /// <summary>Every Expand and Fixed key on word boundaries, longest first.</summary>
    // sea-g2p writes \bk1\b|\bk2\b…; factoring the boundaries out tries the same keys in the same order.
    public static readonly Regex ReAcronymsExceptions = Rx.Build(Rx.BL + "(?:" + string.Join("|",
        Resources.CombinedExceptions.Keys
            .OrderByDescending(RustText.Utf8Len).ThenBy(k => k, StringComparer.Ordinal)
            .Select(Rx.Escape)) + ")" + Rx.BR);

    [GeneratedRegex(@"(?i)" + @"\.(com|vn|net|org|edu|gov|io|biz|info)" + Rx.BR, Rx.Options)]
    private static partial Regex ReDomainSuffixes();

    private static bool HasRomanContext(string preceding)
    {
        var words = RustText.SplitWhitespace(preceding);
        var last = words.Length == 0 ? "" : words[^1];
        last = RustText.ToLower(RustText.TrimMatches(last, c => !RustText.IsAlphanumeric(c)));
        return last.Length > 0 && Resources.RomanKeywords.Contains(last);
    }

    /// <summary>Value of a Roman numeral; unknown letters count as zero.</summary>
    public static int RomanToInt(string s)
    {
        var chars = RustText.ToUpper(s);
        var result = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            var val = Resources.RomanNumerals.GetValueOrDefault(chars[i], 0);
            if (i + 1 < chars.Length && val < Resources.RomanNumerals.GetValueOrDefault(chars[i + 1], 0)) result -= val;
            else result += val;
        }
        return result;
    }

    private static string ExpandRoman(string s)
    {
        if (s.Length == 0) return "";
        var v = RomanToInt(s);
        return v == 0 ? s : " " + Num2Vi.N2W(v.ToString()) + " ";
    }

    private static string ExpandUnitPowers(string text) => ReUnitPowers().Replace(text, m =>
    {
        var b = m.V(1);
        var power = m.V(2);
        var powerNorm = power.StartsWith('-')
            ? "trừ " + Num2Vi.N2W(power[1..])
            : Num2Vi.N2W(power.Replace("+", "", StringComparison.Ordinal));
        var lower = RustText.ToLower(b);
        var full = Resources.MeasurementKeyVi.TryGetValue(lower, out var unit) ? unit
            : Resources.CurrencyKey.TryGetValue(lower, out var cur) ? cur : b;
        return " " + full + " mũ " + powerNorm + " ";
    });

    private static string ExpandLetter(string text) => ReLetter().Replace(text, m =>
    {
        var prefix = m.V(1);
        return Resources.ViLetterNames.TryGetValue(RustText.ToLower(m.V(3)), out var name) ? prefix + " " + name + " " : m.Value;
    });

    public static string ExpandAbbreviations(string text)
    {
        foreach (var (k, v) in Resources.Abbrs) text = text.Replace(k, v, StringComparison.Ordinal);
        return text;
    }

    /// <summary>Single letters become Vietnamese letter names; a capital's period mid-sentence ("R. Nguyễn") is dropped.</summary>
    public static string ExpandStandaloneLetters(string text)
    {
        var endPos = text.TrimEnd().Length;
        return ReStandaloneLetter().Replace(text, m =>
        {
            var raw = m.V(1);
            var dot = m.V(2);
            if (!Resources.ViLetterNames.TryGetValue(RustText.ToLower(raw), out var name)) return m.Value;
            var atEnd = m.Index + m.Length >= endPos;
            if (RustText.IsAsciiUpper(raw[0]) && dot == "." && !atEnd) return " " + name + " ";
            return " " + name + dot + " ";
        });
    }

    private static string ExpandAlphanumeric(string text)
    {
        var lowerText = RustText.ToLower(text);
        return ReAlphanumeric().Replace(text, m =>
        {
            var num = m.V(1);
            var ch = RustText.ToLower(m.V(2));
            if (!Resources.ViLetterNames.TryGetValue(ch, out var name)) return m.Value;
            if (ch == "d" && (lowerText.Contains("quốc lộ", StringComparison.Ordinal) || lowerText.Contains("ql", StringComparison.Ordinal)))
                name = "đê";
            return num + " " + name;
        });
    }

    private static string ExpandEnglishAmpersand(string text) => ReAmpersandAcronym().Replace(text, m =>
    {
        var l = m.V(1);
        var r = m.V(2);
        if (!Resources.EnglishAmpersand.Contains(RustText.ToUpper(l) + RustText.ToUpper(r))) return m.Value;
        static string Spell(string s) => string.Join(" ", RustText.Chars(s).Select(RustText.ToLower));
        return " " + Technical.EnMarked(Spell(l) + " and " + Spell(r)) + " ";
    });

    private static string ExpandSymbols(string text)
    {
        var res = text.Replace("<>", " khác ", StringComparison.Ordinal);
        var sb = new StringBuilder(res.Length + 16);
        foreach (var c in res)
        {
            if (Resources.SymbolsMap.TryGetValue(c, out var v)) sb.Append(v);
            else if (Resources.SuperscriptsMap.TryGetValue(c, out v)) sb.Append(v);
            else if (Resources.SubscriptsMap.TryGetValue(c, out v)) sb.Append(v);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ExpandPrime(string text)
    {
        var res = RePrime().Replace(text, m =>
        {
            var val = RustText.ToLower(m.V(1));
            var name = RustText.IsAsciiDigit(val[0]) ? Num2Vi.N2WSingle(val) : Resources.ViLetterNames.GetValueOrDefault(val, val);
            var count = RustText.CharCount(m.V(2));
            return name + " " + string.Join(" ", Enumerable.Repeat("phẩy", count));
        });
        return RePrimeDigit().Replace(res, m =>
        {
            var q = m.V(1);
            return q == "\"" || q == "”" || RustText.Utf8Len(q) > 1 ? " phẩy phẩy " : " phẩy ";
        });
    }

    private static string ExpandTemperatures(string text)
    {
        var res = ReTempCNeg().Replace(text, "âm $1 độ xê");
        res = ReTempFNeg().Replace(res, "âm $1 độ ép");
        res = ReTempC().Replace(res, "$1 độ xê");
        res = ReTempF().Replace(res, "$1 độ ép");
        return res.Replace("°", " độ ", StringComparison.Ordinal);
    }

    public static string NormalizeOthers(PhonemeDictionary dict, string text, bool enCtx)
    {
        text = ReTitleDot().Replace(text, "$1 ");
        if (!enCtx)
        {
            text = ReDistrictDot().Replace(text, "quận ");
            text = ReWardDot().Replace(text, "phường ");
        }
        text = ReAbs().Replace(text, " giá trị tuyệt đối của $1 ");
        var res = enCtx
            ? text
            : ReAcronymsExceptions.Replace(text, m => Resources.CombinedExceptions.GetValueOrDefault(m.Value, m.Value));

        res = Technical.NormalizeSlashes(res);
        res = ReDomainSuffixes().Replace(res, m =>
        {
            var suffix = Resources.DomainSuffixMap.GetValueOrDefault(RustText.ToLower(m.V(1)), "");
            return " chấm " + (suffix.Length == 0 ? m.V(1) : suffix) + " ";
        });

        res = ReRomanListMarker().Replace(res, m =>
        {
            var lead = m.V(1);
            var roman = m.V(2);
            var tail = m.V(3);
            var headUpper = m.G(4) ?? "";
            var value = RomanToInt(roman);
            var single = RustText.CharCount(roman) == 1;
            if (value <= 0 || value > RomanMarkerMax || (single && RustText.CharCount(headUpper) < 2)) return m.Value;
            return lead + Num2Vi.N2W(value.ToString()) + tail;
        });

        var romanSrc = res;
        res = ReRomanNumber().Replace(romanSrc, m => HasRomanContext(romanSrc[..m.Index]) ? ExpandRoman(m.Value) : m.Value);

        var roman1Src = res;
        res = ReRomanSingle().Replace(roman1Src, m =>
            HasRomanContext(roman1Src[..m.Index]) ? Num2Vi.N2W(RomanToInt(m.Value).ToString()) : m.Value);

        res = ExpandLetter(res);
        res = ExpandAlphanumeric(res);
        res = ReLetterDigit().Replace(res, m =>
            Resources.ViLetterNames.TryGetValue(RustText.ToLower(m.V(1)), out var name) ? name + " " + Num2Vi.N2W(m.V(2)) : m.Value);
        res = ExpandPrime(res);
        res = ExpandUnitPowers(res);
        res = ReCleanQuotes().Replace(res, "");
        res = ReCleanQuotesEdges().Replace(res, "$1 $2");
        res = ExpandEnglishAmpersand(res);
        res = ExpandSymbols(res);
        res = ReBrackets().Replace(res, ", $1, ");
        res = ReStripBrackets().Replace(res, " ");
        res = ExpandTemperatures(res);
        res = Acronyms.NormalizeAcronyms(dict, res, enCtx);

        res = ReVersion().Replace(res, m => string.Join(" chấm ", m.V(1).Split('.')
            .Select(p => string.Join(" ", p.Select(c => Num2Vi.N2WSingle(c.ToString()))))));

        res = ReRatioMulti().Replace(res, m => " " + string.Join(" trên ", m.Value.Split(':').Select(Num2Vi.N2W)) + " ");

        res = ReStandardColon().Replace(res, m =>
        {
            var n1 = m.V(1);
            var n2 = m.V(2);
            var n1Val = RustText.TryParseU64(n1, out var v) ? v : 0;
            if (n1Val < 1000 && !n2.Contains('.')) return " " + n1 + " trên " + n2 + " ";
            return n1 + ", " + n2;
        });

        res = ReColonSemicolon().Replace(res, ", ");
        return ReCleanOthers.Replace(res, " ");
    }
}
