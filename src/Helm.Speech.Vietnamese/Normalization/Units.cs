// Port of sea-g2p src/lang/vi/units.rs.
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Measurement units and currencies (pipeline stage 13). A single uppercase letter glued to a number is an identifier
/// by default ("căn hộ 51M") and is spelled; it becomes a unit only with evidence: a decimal ("3.2M") or a container
/// word before L ("chai 2L"). W is always watts, G is never grams, M/B/K never a magnitude. Lowercase always means the
/// unit ("24h", "450g").
/// </summary>
internal static partial class Units
{
    private static string ExpandScientific(string num)
    {
        var eIdx = RustText.ToLower(num).IndexOf('e');
        var b = num[..eIdx];
        var exp = num[(eIdx + 1)..];

        string baseNorm;
        if (b.Contains('.'))
        {
            var parts = b.Split('.');
            var dec = parts[1].TrimEnd('0');
            baseNorm = dec.Length > 0 ? Num2Vi.N2W(parts[0]) + " chấm " + Num2Vi.N2WDecimal(dec) : Num2Vi.N2W(parts[0]);
        }
        else if (b.Contains(','))
        {
            var parts = b.Split(',');
            var dec = parts[1].TrimEnd('0');
            baseNorm = dec.Length > 0 ? Num2Vi.N2W(parts[0]) + " phẩy " + Num2Vi.N2WDecimal(dec) : Num2Vi.N2W(parts[0]);
        }
        else
        {
            baseNorm = Num2Vi.N2W(b.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal));
        }

        var expVal = exp.TrimStart('+');
        var expNorm = expVal.StartsWith('-') ? "trừ " + Num2Vi.N2W(expVal[1..]) : Num2Vi.N2W(expVal);
        return baseNorm + " nhân mười mũ " + expNorm;
    }

    private static string ExpandMixedSep(string num)
    {
        var rDot = Math.Max(num.LastIndexOf('.'), 0);
        var rComma = Math.Max(num.LastIndexOf(','), 0);
        string[] parts = rDot > rComma
            ? num.Replace(",", "", StringComparison.Ordinal).Split('.')
            : num.Replace(".", "", StringComparison.Ordinal).Split(',');
        if (parts.Length < 2)
            return Num2Vi.N2W(num.Replace(",", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal));
        var dec = parts[1].TrimEnd('0');
        return dec.Length == 0 ? Num2Vi.N2W(parts[0]) : Num2Vi.N2W(parts[0]) + " phẩy " + Num2Vi.N2WDecimal(dec);
    }

    private static string ExpandSingleSep(string num)
    {
        if (num.Contains(','))
        {
            var parts = num.Split(',');
            if (parts.Length > 2 || (parts.Length == 2 && RustText.Utf8Len(parts[1]) == 3))
                return Num2Vi.N2W(num.Replace(",", "", StringComparison.Ordinal));
            var dec = parts[1].TrimEnd('0');
            if (dec.Length == 0) return Num2Vi.N2W(parts[0]);
            return Num2Vi.N2W(parts[0]) + " phẩy " + Num2Vi.N2WDecimal(dec);
        }
        var p = num.Split('.');
        if (p.Length > 2 || (p.Length == 2 && RustText.Utf8Len(p[1]) == 3))
            return Num2Vi.N2W(num.Replace(".", "", StringComparison.Ordinal));
        var d = p[1].TrimEnd('0');
        if (d.Length == 0) return Num2Vi.N2W(p[0]);
        return Num2Vi.N2W(p[0]) + " chấm " + Num2Vi.N2WDecimal(d);
    }

    public static string ExpandNumberWithSep(string num)
    {
        if (num.Length == 0) return "";
        if (RustText.ToLower(num).Contains('e')) return ExpandScientific(num);
        if (num.Contains(',') && num.Contains('.')) return ExpandMixedSep(num);
        if (num.Contains(',') || num.Contains('.')) return ExpandSingleSep(num);
        return Num2Vi.N2W(num);
    }

    /// <summary>Every unit and currency key, lowercased, with its reading ("%" left out; "m" forced to "mét").</summary>
    private static readonly Dictionary<string, string> AllUnitsMap = BuildAllUnits();

    private static Dictionary<string, string> BuildAllUnits()
    {
        var m = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in Resources.MeasurementKeyVi) m[RustText.ToLower(k)] = v;
        foreach (var (k, v) in Resources.CurrencyKey)
            if (k != "%") m[RustText.ToLower(k)] = v;
        m["m"] = "mét";
        return m;
    }

    internal static bool IsUnitKey(string lowered) => AllUnitsMap.ContainsKey(lowered);

    private static string UnitsPattern()
    {
        var keys = Resources.MeasurementKeyVi.Keys.ToList();
        foreach (var k in Resources.CurrencyKey.Keys)
            if (k != "%") keys.Add(k);
        // Longest first, as sea-g2p sorts them; keys of equal length cannot both match at one position.
        var sorted = keys.OrderByDescending(RustText.Utf8Len).ThenBy(k => k, StringComparer.Ordinal);
        // Under (?i) Rust folds the micro sign onto Greek mu (µ, μ and Μ are one class); .NET does not.
        return string.Join("|", sorted.Select(k =>
            string.Concat(Rx.Escape(k).Select(c => c is 'µ' or 'μ' ? "[µμΜ]" : c.ToString()))));
    }

    private const string NumericP = @"(\d+(?:[.,]\d+)*)";
    private const string MagnitudeP = @"(?:\s*(tỷ|triệu|nghìn|ngàn))?";

    private static readonly Regex ReCompoundUnit = Rx.Build(@"(?i)" + Rx.BL + NumericP + @"?\s*([a-zμµ²³°]+)/([a-zμµ²³°0-9]+)" + Rx.B);

    private static readonly Regex ReUnitsWithNum =
        Rx.Build(@"(?i)(?<![a-zA-Z\d.,])" + NumericP + MagnitudeP + @"\s*(" + UnitsPattern() + @")" + Rx.B);

    private static readonly Regex ReStandaloneUnit =
        Rx.Build(@"(?i)(?<![\d.,])" + Rx.BL + @"(km|cm|mm|kg|mg|usd|vnd|ph|mmhg|cmh2o)" + Rx.BR);

    private static readonly Regex ReCurrencyPrefixSymbol =
        Rx.Build(@"(?i)([$€¥£₩₫])\s*" + NumericP + MagnitudeP + @"(?:([MBK])" + Rx.BR + @")?");

    private static readonly Regex ReCurrencySuffixSymbol = Rx.Build(@"(?i)" + NumericP + MagnitudeP + @"([$€¥£₩₫])");

    private static readonly Regex RePercentage = Rx.Build(@"(?i)" + NumericP + @"\s*%");

    [GeneratedRegex(@"(?i)" + Rx.BL + @"\d{1,3}(?:,\d{3})+(?:\.\d+)?" + Rx.BR, Rx.Options)]
    private static partial Regex ReEnglishStyleNumbers();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(\d+(?:[.,]\d+)?)\s*[x*×]\s*10\^([-+]?\d+)" + Rx.BR, Rx.Options)]
    private static partial Regex RePowerOfTen();

    [GeneratedRegex(@"(?i)([-–—])?(\d+(?:[.,]\d+)?e[+-]?\d+)", Rx.Options)]
    private static partial Regex ReScientificNotation();

    [GeneratedRegex(@"(?<![\d.,a-zA-Z])(\d{1,2})m(\d{2}|[01456789])(?!\d|[.,]\d)", Rx.Options)]
    private static partial Regex ReHeight();

    [GeneratedRegex(@"(?<![\d.,a-zA-Z])(\d{1,2})kg(\d{1,2})(?!\d|[.,]\d)", Rx.Options)]
    private static partial Regex ReWeight();

    public static string ExpandHeightWeight(string text)
    {
        var res = ReWeight().Replace(text, m => " " + Num2Vi.N2W(m.V(1)) + " ki lô gam " + Num2Vi.N2W(m.V(2)) + " ");
        return ReHeight().Replace(res, m => " " + Num2Vi.N2W(m.V(1)) + " mét " + Num2Vi.N2W(m.V(2)) + " ");
    }

    /// <summary>A container or measuring verb before the number makes "L" mean litres ("chai 2L").</summary>
    private static readonly HashSet<string> ContainerLeadWords = new(
        ["chai", "bình", "thùng", "can", "xô", "bồn", "két", "lu", "ấm", "nồi", "tích", "chứa", "đựng", "đổ", "uống"],
        StringComparer.Ordinal);

    private static string LastWordBefore(string text, int pos)
    {
        var words = RustText.SplitWhitespace(text[..pos]);
        return words.Length == 0 ? "" : RustText.ToLower(RustText.TrimMatches(words[^1], c => !RustText.IsAlphanumeric(c)));
    }

    private static string FirstWordAfter(string text, int pos)
    {
        var words = RustText.SplitWhitespace(text[pos..]);
        return words.Length == 0 ? "" : RustText.ToLower(RustText.TrimMatches(words[0], c => !RustText.IsAlphanumeric(c)));
    }

    private static string Tidy(string s) => s.Replace("  ", " ", StringComparison.Ordinal).Trim();

    public static string ExpandUnitsAndCurrency(string text)
    {
        var result = ReCurrencyPrefixSymbol.Replace(text, m =>
        {
            var symbol = m.V(1);
            var num = m.V(2);
            var mag = m.G(3) ?? "";
            var suffix = m.G(4) ?? "";
            var magWord = RustText.ToUpper(suffix) switch
            {
                "M" => "triệu",
                "B" => "tỷ",
                "K" => "nghìn",
                _ => mag,
            };
            var full = Resources.CurrencySymbolMap.GetValueOrDefault(symbol, "");
            return Tidy(ExpandNumberWithSep(num) + " " + magWord + " " + full);
        });

        result = ReCurrencySuffixSymbol.Replace(result, m =>
        {
            var num = m.V(1);
            var mag = m.G(2) ?? "";
            var full = Resources.CurrencySymbolMap.GetValueOrDefault(m.V(3), "");
            return Tidy(ExpandNumberWithSep(num) + " " + mag + " " + full);
        });

        result = RePercentage.Replace(result, m => ExpandNumberWithSep(m.V(1)) + " phần trăm");

        var unitsSrc = result;
        result = ReUnitsWithNum.Replace(unitsSrc, m =>
        {
            var num = m.V(1);
            var mag = m.G(2) ?? "";
            var unit = m.V(3);

            var isUpperSingle = RustText.Utf8Len(unit) == 1 && RustText.IsAsciiUpper(unit[0]);
            if (isUpperSingle && unit != "W")
            {
                var isDecimal = num.Contains('.') || num.Contains(',');
                var lead = LastWordBefore(unitsSrc, m.Index);
                var literOk = unit == "L" && ContainerLeadWords.Contains(lead);
                if (unit is "G" or "M" or "B" or "K" || !(isDecimal || literOk)) return m.Value;
            }

            if (RustText.CharCount(unit) == 1)
            {
                var after = FirstWordAfter(unitsSrc, m.Index + m.Length);
                if (RustText.CharCount(after) == 1 && after.All(RustText.IsAsciiAlpha) && after != "x") return m.Value;
            }

            var full = unit == "m" ? "mét" : AllUnitsMap.GetValueOrDefault(RustText.ToLower(unit), unit);
            return Tidy(ExpandNumberWithSep(num) + " " + mag + " " + full);
        });

        return ReStandaloneUnit.Replace(result, m =>
        {
            var unit = m.V(1);
            return " " + AllUnitsMap.GetValueOrDefault(RustText.ToLower(unit), unit) + " ";
        });
    }

    public static string ExpandCompoundUnits(string text) => ReCompoundUnit.Replace(text, m =>
    {
        var numStr = m.G(1) ?? "";
        var u1 = m.V(2);
        var u2 = m.V(3);

        static string GetUnit(string u)
        {
            if (u == "M") return "triệu";
            if (u == "m") return "mét";
            return AllUnitsMap.GetValueOrDefault(RustText.ToLower(u), u);
        }

        if (numStr.Length == 0)
        {
            var u1Lower = RustText.ToLower(u1);
            var u2Lower = RustText.ToLower(u2);
            var u1IsUnit = AllUnitsMap.ContainsKey(u1Lower);
            var u2IsUnit = AllUnitsMap.ContainsKey(u2Lower);
            if (RustText.Utf8Len(u1) == 1 && RustText.Utf8Len(u2) == 1 && (!u1IsUnit || !u2IsUnit))
            {
                var l1 = Resources.ViLetterNames.GetValueOrDefault(u1Lower, u1);
                var l2 = Resources.ViLetterNames.GetValueOrDefault(u2Lower, u2);
                return " " + l1 + " trên " + l2 + " ";
            }
            if (u1IsUnit && u2IsUnit) return " " + GetUnit(u1) + " trên " + GetUnit(u2) + " ";
            return m.Value;
        }
        return ExpandNumberWithSep(numStr) + " " + GetUnit(u1) + " trên " + GetUnit(u2) + " ";
    });

    public static string FixEnglishStyleNumbers(string text) => ReEnglishStyleNumbers().Replace(text, m =>
    {
        var val = m.Value;
        var hasComma = val.Contains(',');
        var hasDot = val.Contains('.');
        if (val.Count(c => c == ',') > 1 || (hasComma && hasDot && val.IndexOf(',') < val.IndexOf('.')))
        {
            return hasDot
                ? val.Replace(",", "", StringComparison.Ordinal).Replace(".", ",", StringComparison.Ordinal)
                : val.Replace(",", "", StringComparison.Ordinal);
        }
        if (hasComma && hasDot)
            return val.Replace(",", "", StringComparison.Ordinal).Replace(".", ",", StringComparison.Ordinal);
        return val;
    });

    public static string ExpandPowerOfTen(string text) => RePowerOfTen().Replace(text, m =>
    {
        var baseNorm = ExpandNumberWithSep(m.V(1));
        var expVal = m.V(2).Replace("+", "", StringComparison.Ordinal);
        var expNorm = expVal.StartsWith('-') ? "trừ " + Num2Vi.N2W(expVal[1..]) : Num2Vi.N2W(expVal);
        return " " + baseNorm + " nhân mười mũ " + expNorm + " ";
    });

    public static string ExpandScientificNotation(string text) => ReScientificNotation().Replace(text, m =>
    {
        var neg = m.G(1) ?? "";
        var expanded = ExpandNumberWithSep(m.V(2));
        return neg.Length > 0 ? " âm " + expanded + " " : " " + expanded + " ";
    });
}
