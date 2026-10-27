// Port of sea-g2p src/lang/vi/num2vi.rs.
using System.Text;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Vietnamese number words. Quantities read as cardinals ("1250" is "một nghìn hai trăm năm mươi"); identifiers such as
/// phone numbers read figure by figure. Vietnamese changes a digit's word by position: a final 1 after a tens digit is
/// "mốt", a final 5 is "lăm", and an inner zero becomes "lẻ".
/// </summary>
internal static class Num2Vi
{
    public static string Units(char digit) => digit switch
    {
        '0' => "không",
        '1' => "một",
        '2' => "hai",
        '3' => "ba",
        '4' => "bốn",
        '5' => "năm",
        '6' => "sáu",
        '7' => "bảy",
        '8' => "tám",
        '9' => "chín",
        _ => "",
    };

    /// <summary>One group of up to three digits; "không trăm" only when the group is a full three digits.</summary>
    public static string Hundreds(string numbers)
    {
        if (numbers.Length == 0 || numbers == "000") return "";
        var n = numbers.PadLeft(3, '0');
        var h = n[0];
        var t = n[1];
        var u = n[2];
        var res = new List<string>(4);

        if (h != '0') res.Add(Units(h) + " trăm");
        else if (numbers.Length == 3) res.Add("không trăm");

        if (t == '0')
        {
            if (u != '0' && (h != '0' || numbers.Length == 3)) res.Add("lẻ");
        }
        else if (t == '1') res.Add("mười");
        else res.Add(Units(t) + " mươi");

        if (u != '0')
        {
            if (u == '1' && t != '0' && t != '1') res.Add("mốt");
            else if (u == '5' && t != '0') res.Add("lăm");
            else res.Add(Units(u));
        }
        return string.Join(" ", res);
    }

    public static string LargeNumber(string numbers)
    {
        numbers = numbers.TrimStart('0');
        if (numbers.Length == 0) return Units('0');

        var groups = new List<string>();
        for (var i = numbers.Length; i > 0; i -= 3)
        {
            var start = Math.Max(0, i - 3);
            groups.Add(numbers[start..i]);
        }

        string[] suffixes = ["", " nghìn", " triệu", " tỷ"];
        var parts = new List<string>();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group == "000") continue;
            var word = Hundreds(group);
            if (word.Length == 0) continue;
            var sb = new StringBuilder(word).Append(suffixes[i % 3]);
            for (var k = 0; k < i / 3; k++) sb.Append(" tỷ");
            parts.Add(sb.ToString());
        }
        if (parts.Count == 0) return Units('0');
        parts.Reverse();
        return string.Join(" ", parts).Trim();
    }

    private static string AsciiDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c is >= '0' and <= '9') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Cardinal reading; "05" reads "không năm"; text without digits comes back unchanged.</summary>
    public static string N2W(string number)
    {
        var clean = AsciiDigits(number);
        if (clean.Length == 0) return number;
        if (clean.Length == 2 && clean[0] == '0') return "không " + Units(clean[1]);
        return LargeNumber(clean);
    }

    /// <summary>Figure-by-figure reading for identifiers; a "+84" prefix is read as a leading zero.</summary>
    public static string N2WSingle(string number)
    {
        var s = number.StartsWith("+84", StringComparison.Ordinal) ? "0" + number[3..] : number;
        var res = new List<string>();
        foreach (var c in s)
            if (c is >= '0' and <= '9') res.Add(Units(c));
        return res.Count == 0 ? number : string.Join(" ", res);
    }

    /// <summary>Digits after a decimal mark, figure by figure, with a final 5 after a non-zero digit read "lăm".</summary>
    public static string N2WDecimal(string number)
    {
        var clean = AsciiDigits(number);
        if (clean.Length == 0) return number;
        var res = new List<string>(clean.Length);
        for (var i = 0; i < clean.Length; i++)
        {
            var d = clean[i];
            if (d == '5' && i == clean.Length - 1 && i > 0 && clean[i - 1] != '0') res.Add("lăm");
            else
            {
                var u = Units(d);
                if (u.Length > 0) res.Add(u);
            }
        }
        return string.Join(" ", res);
    }
}
