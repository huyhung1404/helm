// Port of sea-g2p src/lang/vi/misc.rs (licence plates, codes, weekday abbreviations, clothing sizes).
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Identifier-shaped tokens whose digits are figures, not quantities: licence plates ("51H-123.45"), letter-digit codes
/// ("ABC-1234"), order numbers ("#45021") and service numbers. Also the weekday abbreviations T2-T7 and CN, read as
/// days only after a time cue ("sáng T2"), and clothing sizes after "size" or "cỡ".
/// </summary>
internal static partial class Codes
{
    [GeneratedRegex(Rx.BL + @"(\d{2})([A-Z]{1,2}\d?)\s*[-–]\s*(\d{3}\.\d{2}|\d{2,5})" + Rx.BR, Rx.Options)]
    private static partial Regex RePlate();

    [GeneratedRegex(Rx.BL + @"([Bb]iển(?:\s+số|\s+kiểm\s+soát)?|BKS)\s+(\d{2})([A-Z]{1,2}\d?)" + Rx.BR, Rx.Options)]
    private static partial Regex RePlateLead();

    [GeneratedRegex(Rx.BL + @"([A-Z]{2,6})-(\d{3,6})" + Rx.BR, Rx.Options)]
    private static partial Regex ReCodeDigits();

    [GeneratedRegex(@"#(\d{3,8})" + Rx.BR, Rx.Options)]
    private static partial Regex ReHashId();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(tổng đài|hotline|đầu số)\s+(\d{3,8}(?:\.\d{2,6})*)" + Rx.BR, Rx.Options)]
    private static partial Regex ReHotline();

    private static string SpellPlateSerie(string serie) => string.Join(" ", RustText.Chars(serie).Select(ch =>
    {
        if (ch.Length == 1 && RustText.IsAsciiDigit(ch[0])) return Num2Vi.N2WSingle(ch);
        var cl = RustText.ToLower(ch);
        return Resources.ViLetterNames.GetValueOrDefault(cl, cl);
    }));

    private static string SpellPlateTail(string tail) =>
        string.Join(" ", RustText.Chars(tail).Select(ch => ch == "." ? "chấm" : Num2Vi.N2WSingle(ch)));

    public static string ExpandCodesAndPlates(string text)
    {
        var res = RePlate().Replace(text, m =>
            Num2Vi.N2W(m.V(1)) + " " + SpellPlateSerie(m.V(2)) + " " + SpellPlateTail(m.V(3)));
        res = RePlateLead().Replace(res, m => m.V(1) + " " + Num2Vi.N2W(m.V(2)) + " " + SpellPlateSerie(m.V(3)));
        res = ReCodeDigits().Replace(res, m => m.V(1) + " " + Num2Vi.N2WSingle(m.V(2)));
        res = ReHashId().Replace(res, m => Num2Vi.N2WSingle(m.V(1)));
        res = ReHotline().Replace(res, m => m.V(1) + " " + Num2Vi.N2WSingle(m.V(2)));
        return res;
    }

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(sáng|trưa|chiều|tối|đêm|hôm|ngày|từ|đến|tới|vào|mỗi|hằng|nghỉ)\s+(?:T([2-7])|CN)" + Rx.BR, Rx.Options)]
    private static partial Regex ReWeekdayLead();

    [GeneratedRegex(@"(thứ (?:hai|ba|tư|năm|sáu|bảy))(\s*(?:,|và|-|–|đến|tới)\s*)(?:T([2-7])|CN)" + Rx.BR, Rx.Options)]
    private static partial Regex ReWeekdayChain();

    private static string WeekdayName(string? d) => d switch
    {
        "2" => "thứ hai",
        "3" => "thứ ba",
        "4" => "thứ tư",
        "5" => "thứ năm",
        "6" => "thứ sáu",
        "7" => "thứ bảy",
        _ => "chủ nhật",
    };

    /// <summary>T2..T7 and CN become weekdays after a time cue, and lists after one inherit it ("thứ hai, T4 và CN").</summary>
    public static string ExpandWeekdayAbbr(string text)
    {
        if (!text.Contains('T') && !text.Contains("CN", StringComparison.Ordinal)) return text;
        var res = ReWeekdayLead().Replace(text, m => m.V(1) + " " + WeekdayName(m.G(2)));
        for (var i = 0; i < 6; i++)
        {
            var next = ReWeekdayChain().Replace(res, m => m.V(1) + m.V(2) + WeekdayName(m.G(3)));
            if (next == res) break;
            res = next;
        }
        return res;
    }

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(size|cỡ)\s+((?:xxxl|xxl|xl|xs|[sml])(?:\s*/\s*(?:xxxl|xxl|xl|xs|[sml]))*)" + Rx.BR, Rx.Options)]
    private static partial Regex ReSizeLabel();

    /// <summary>"size M/L/XL" spelled as letters: Vietnamese names, or English ones in an English sentence.</summary>
    public static string ExpandSizeLabels(string text, bool enCtx) => ReSizeLabel().Replace(text, m =>
    {
        var spelled = m.V(2).Split('/').Select(s =>
        {
            var size = s.Trim();
            if (enCtx) return "__start_en__" + string.Join(" ", RustText.Chars(RustText.ToLower(size))) + "__end_en__";
            return Acronyms.SpellVi(size);
        });
        return m.V(1) + " " + string.Join(" ", spelled);
    });
}
