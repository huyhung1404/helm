// Port of sea-g2p src/lang/vi/datestime.rs.
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Dates and clock times (pipeline stage 10). "d/m" and a fraction are written the same, so the reading rests on
/// context: a leading zero ("01/8"), a lead word right before ("chiều 17/10"), or a date keyword within three words.
/// Mathematical context forces the fraction. Expanding a date inserts "ngày", and the duplicate it may create with the
/// author's own "ngày" is collapsed only when a number follows, because "ngày ngày" is ordinary Vietnamese too.
/// </summary>
internal static partial class DatesTimes
{
    private static readonly int[] DayInMonth = [31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    [GeneratedRegex(@"(?<![a-zA-Z\d])(?<![a-zA-Z\d][.,])(\d{1,2})(\/|-|\.){1}(\d{1,2})(\/|-|\.){1}(\d{4})(?!\d|[.,]\d)", Rx.Options)]
    private static partial Regex ReFullDate();

    [GeneratedRegex(@"(?<![a-zA-Z\d])(?<![a-zA-Z\d][.,])(\d{4})-(\d{2})-(\d{2})(?!\d|[.,]\d)", Rx.Options)]
    private static partial Regex ReYyyyMmDd();

    [GeneratedRegex(@"(\d{2})T(\d{2})|(\d{2})Z" + Rx.BR, Rx.Options)]
    private static partial Regex ReIsoFix();

    [GeneratedRegex(@"(?<![a-zA-Z\d])(?<![a-zA-Z\d][.,])(\d{1,2})(\/|-){1}(\d{1,2})(?!\d|[.,]\d)", Rx.Options)]
    private static partial Regex ReDayMonth();

    [GeneratedRegex(@"(?<![a-zA-Z\d])(?<![a-zA-Z\d][.,])(\d{1,2})(\/|-|\.){1}(\d{4})(?!\d|[.,]\d)", Rx.Options)]
    private static partial Regex ReMonthYear();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"([a-zA-Z]\d*)/(\d{4})" + Rx.BR, Rx.Options)]
    private static partial Regex RePeriodYear();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(quý)\s+(\d{1,2}|IV|III|II|I)\s*/\s*(\d{4})" + Rx.BR, Rx.Options)]
    private static partial Regex ReQuyYear();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(\d+)(g|:|h)(\d{1,2})(p|:|m)(\d{1,2})(?:\s*(giây|s|g))?" + Rx.BR, Rx.Options)]
    private static partial Regex ReFullTime();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(\d+)(g|h|:)(\d{1,2})(?:\s*(phút|p|m|giây|s|g))?" + Rx.BR + @"(?![.,]\d)", Rx.Options)]
    private static partial Regex ReTime();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(\d+)g\s*(sáng|trưa|chiều|tối|khuya)" + Rx.BR, Rx.Options)]
    private static partial Regex ReHourContext();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"lúc\s*(\d+)g" + Rx.BR, Rx.Options)]
    private static partial Regex ReLucHour();

    /// <summary>Number words: a duplicated lead word is collapsed only when one of these follows.</summary>
    public static readonly HashSet<string> NumWords = new(
    [
        "không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín",
        "mười", "mươi", "mốt", "lăm", "tư", "trăm", "nghìn", "ngàn", "triệu", "tỷ",
    ], StringComparer.Ordinal);

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(từ|ngày|hôm)\s+(\d{1,2})/(\d{1,2})\s*(đến hết|đến|tới|-|–)\s*(\d{1,2})/(\d{1,2})" + Rx.BR, Rx.Options)]
    private static partial Regex ReDateRange();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"ngày\s+ngày\s+(\S+)", Rx.Options)]
    private static partial Regex ReRedundantNgay();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"tháng\s+tháng\s+(\S+)", Rx.Options)]
    private static partial Regex ReRedundantThang();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"năm\s+năm\s+(\S+)", Rx.Options)]
    private static partial Regex ReRedundantNam();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"hôm\s+ngày\s+(\S+)", Rx.Options)]
    private static partial Regex ReRedundantHomNgay();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(mùng|mồng)\s+ngày\s+(\S+)", Rx.Options)]
    private static partial Regex ReRedundantMungNgay();

    private static string DedupLead(string text, Regex re, string keep) => re.Replace(text, m =>
    {
        var next = m.V(1);
        return NumWords.Contains(RustText.ToLower(next)) ? keep + " " + next : m.Value;
    });

    private static bool IsValidDate(string day, string month)
    {
        var d = RustText.ParseI32(day, 0);
        var mo = RustText.ParseI32(month, 0);
        if (mo >= 1 && mo <= 12) return d >= 1 && d <= DayInMonth[mo - 1];
        return false;
    }

    private static readonly Func<int, bool> ContextTrim = c => c < 0x10000 && ",.!?;()[]{}".Contains((char)c);

    private static List<string> GetContextWords(string text, int start, int end, int window)
    {
        var left = RustText.SplitWhitespace(text[..start]);
        var right = RustText.SplitWhitespace(text[end..]);
        var context = new List<string>();
        var leftStart = left.Length > window ? left.Length - window : 0;
        for (var i = leftStart; i < left.Length; i++) context.Add(RustText.ToLower(RustText.TrimMatches(left[i], ContextTrim)));
        for (var i = 0; i < Math.Min(right.Length, window); i++)
            context.Add(RustText.ToLower(RustText.TrimMatches(right[i], ContextTrim)));
        return context;
    }

    private static string NormTimePart(string s)
    {
        var trimmed = s.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private static string MonthWord(int m) => m == 4 ? "tư" : Num2Vi.N2W(m.ToString());

    private static string DayWord(string d) => Num2Vi.N2W(RustText.ParseI32(d, 0).ToString());

    private static readonly string[] MathSymbols = ["+", "-", "*", "x", "×", "/", "=", ">", "<", "≥", "≤", "≈", "±"];

    public static string NormalizeDate(string text)
    {
        var result = ReQuyYear().Replace(text, m =>
        {
            var q = m.V(2);
            var qNum = q.All(RustText.IsAsciiDigit) ? q : Misc.RomanToInt(RustText.ToUpper(q)).ToString();
            return "quý " + Num2Vi.N2W(qNum) + " năm " + Num2Vi.N2W(m.V(3));
        });

        result = ReIsoFix().Replace(result, m =>
        {
            var g1 = m.G(1);
            if (g1 != null) return g1 + " T " + m.V(2);
            return m.V(3) + " Z ";
        });

        result = ReYyyyMmDd().Replace(result, m =>
        {
            var year = m.V(1);
            var month = m.V(2);
            var day = m.V(3);
            if (!IsValidDate(day, month)) return m.Value;
            return "ngày " + DayWord(day) + " tháng " + MonthWord(RustText.ParseI32(month, 0)) + " năm " + Num2Vi.N2W(year);
        });

        result = RePeriodYear().Replace(result, m =>
        {
            var code = m.V(1);
            var year = m.V(2);
            var codeLower = RustText.ToLower(code);
            string prefix;
            if (codeLower.StartsWith('q') && RustText.Utf8Len(code) <= 2)
            {
                var qNum = code[1..];
                prefix = "quý " + (qNum.Length == 0 ? "" : Num2Vi.N2W(qNum));
            }
            else
            {
                var parts = new List<string>();
                foreach (var ch in RustText.Chars(code))
                {
                    var cl = RustText.ToLower(ch);
                    if (ch.Length == 1 && RustText.IsAsciiDigit(ch[0])) parts.Add(Num2Vi.N2W(ch));
                    else if (Resources.ViLetterNames.TryGetValue(cl, out var name)) parts.Add(name);
                    else parts.Add(cl);
                }
                prefix = string.Join(" ", parts);
            }
            return prefix.Trim() + " " + Num2Vi.N2WDecimal(year);
        });

        result = ReFullDate().Replace(result, m =>
        {
            var day = m.V(1);
            var month = m.V(3);
            var year = m.V(5);
            if (IsValidDate(day, month))
                return "ngày " + DayWord(day) + " tháng " + MonthWord(RustText.ParseI32(month, 0)) + " năm " + Num2Vi.N2W(year);
            return " " + Num2Vi.N2W(day) + " trên " + Num2Vi.N2W(month) + " trên " + Num2Vi.N2W(year) + " ";
        });

        result = ReMonthYear().Replace(result, m =>
        {
            var mo = RustText.ParseI32(m.V(1), 0);
            var y = RustText.ParseI32(m.V(3), 0);
            if (mo >= 1 && mo <= 12 && y <= 2500) return "tháng " + MonthWord(mo) + " năm " + Num2Vi.N2W(y.ToString());
            return m.Value;
        });

        result = ReDateRange().Replace(result, m =>
        {
            var d1 = m.V(2);
            var m1 = m.V(3);
            var d2 = m.V(5);
            var m2 = m.V(6);
            if (!IsValidDate(d1, m1) || !IsValidDate(d2, m2)) return m.Value;
            return m.V(1) + " ngày " + DayWord(d1) + " tháng " + MonthWord(RustText.ParseI32(m1, 0)) + " "
                + m.V(4) + " ngày " + DayWord(d2) + " tháng " + MonthWord(RustText.ParseI32(m2, 0));
        });

        var current = result;
        result = ReDayMonth().Replace(current, m =>
        {
            var dayStr = m.V(1);
            var monthStr = m.V(3);
            var sep = m.V(2);
            var joiner = sep == "-" ? "đến" : "trên";
            var a = RustText.ParseI32(dayStr, 0);
            var b = RustText.ParseI32(monthStr, 0);

            var contextWords = GetContextWords(current, m.Index, m.Index + m.Length, 3);
            var isValid = IsValidDate(dayStr, monthStr);

            var monthLeadingZero = monthStr.StartsWith('0') && RustText.Utf8Len(monthStr) > 1;
            var dayLeadingZero = dayStr.StartsWith('0') && RustText.Utf8Len(dayStr) > 1;
            if (isValid && (monthLeadingZero || dayLeadingZero))
                return "ngày " + Num2Vi.N2W(a.ToString()) + " tháng " + MonthWord(b);

            var before = RustText.SplitWhitespace(current[..m.Index]);
            var leadWord = before.Length == 0
                ? ""
                : RustText.ToLower(RustText.TrimMatches(before[^1], c => !RustText.IsAlphanumeric(c)));
            if (isValid && Resources.DateLeadWords.Contains(leadWord))
                return "ngày " + Num2Vi.N2W(a.ToString()) + " tháng " + MonthWord(b);

            if (isValid && contextWords.Any(Resources.DateKeywords.Contains))
                return "ngày " + Num2Vi.N2W(a.ToString()) + " tháng " + MonthWord(b);

            if (contextWords.Any(Resources.MathKeywords.Contains) || contextWords.Any(w => MathSymbols.Contains(w)))
                return Num2Vi.N2W(dayStr) + " trên " + Num2Vi.N2W(monthStr);

            if (!isValid)
            {
                if (dayStr.StartsWith('0') || monthStr.StartsWith('0'))
                    return Num2Vi.N2W(dayStr) + " " + joiner + " " + Num2Vi.N2W(monthStr);
                return m.Value;
            }
            return Num2Vi.N2W(dayStr) + " " + joiner + " " + Num2Vi.N2W(monthStr);
        });

        result = DedupLead(result, ReRedundantNgay(), "ngày");
        result = DedupLead(result, ReRedundantThang(), "tháng");
        result = DedupLead(result, ReRedundantNam(), "năm");
        result = DedupLead(result, ReRedundantHomNgay(), "hôm");
        result = ReRedundantMungNgay().Replace(result, m =>
        {
            var next = m.V(2);
            return NumWords.Contains(RustText.ToLower(next)) ? m.V(1) + " " + next : m.Value;
        });
        return result;
    }

    public static string NormalizeTime(string text)
    {
        var result = ReFullTime().Replace(text, m =>
        {
            var h = m.V(1);
            var sep1 = m.V(2);
            var mi = m.V(3);
            var sep2 = m.V(4);
            var s = m.V(5);
            if (sep1 == ":" && sep2 == ":" && (RustText.Utf8Len(mi) != 2 || RustText.Utf8Len(s) != 2)) return m.Value;
            return Num2Vi.N2W(NormTimePart(h)) + " giờ " + Num2Vi.N2W(NormTimePart(mi)) + " phút "
                + Num2Vi.N2W(NormTimePart(s)) + " giây";
        });

        result = ReTime().Replace(result, m =>
        {
            var full = m.Value;
            var hStr = m.V(1);
            var sep = m.V(2);
            var mStr = m.V(3);
            var suffix = m.G(4) is { } sfx ? RustText.ToLower(sfx) : "";
            var hInt = RustText.ParseI32(hStr, -1);
            var mInt = RustText.ParseI32(mStr, -1);

            if (sep == ":" && RustText.Utf8Len(mStr) != 2 && suffix.Length == 0) return full;
            if (sep is "H" or "G" or "g" or "h" && suffix.Length == 0 && RustText.Utf8Len(mStr) == 1) return full;

            if (mInt >= 0 && mInt < 60)
            {
                var isMinSec = sep == ":" && hInt >= 24;
                var hWords = isMinSec ? Num2Vi.N2W(hStr) : Num2Vi.N2W(NormTimePart(hStr));
                var mWords = Num2Vi.N2W(NormTimePart(mStr));
                var hUnit = isMinSec ? "phút" : "giờ";
                var mUnit = isMinSec ? "giây" : "phút";
                return hWords + " " + hUnit + " " + mWords + " " + mUnit;
            }
            return full;
        });

        result = ReHourContext().Replace(result, m => Num2Vi.N2W(m.V(1)) + " giờ " + m.V(2));
        result = ReLucHour().Replace(result, m => "lúc " + Num2Vi.N2W(m.V(1)) + " giờ");
        return result;
    }
}
