// Port of sea-g2p src/lang/vi/num2en.rs.
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// English readings for numbers and symbols. A sentence with no Vietnamese diacritics and enough English words is read
/// as English, so its numbers, percentages, currency, units and clock times become English words before any Vietnamese
/// pass runs, and those passes then find no digits left. Also reads "text2text" and "sale4u" in any sentence.
/// </summary>
internal static partial class Num2En
{
    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
        "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] Tens =
        ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static string TwoDigits(ulong n)
    {
        if (n < 20) return Ones[n];
        if (n % 10 == 0) return Tens[n / 10];
        return Tens[n / 10] + " " + Ones[n % 10];
    }

    private static string ThreeDigits(ulong n)
    {
        if (n < 100) return TwoDigits(n);
        if (n % 100 == 0) return Ones[n / 100] + " hundred";
        return Ones[n / 100] + " hundred " + TwoDigits(n % 100);
    }

    public static string IntWords(ulong n)
    {
        if (n == 0) return "zero";
        (ulong Value, string Name)[] scales =
        [
            (1_000_000_000_000, "trillion"),
            (1_000_000_000, "billion"),
            (1_000_000, "million"),
            (1_000, "thousand"),
        ];
        var parts = new List<string>();
        foreach (var (v, name) in scales)
        {
            if (n >= v)
            {
                parts.Add(ThreeDigits(n / v) + " " + name);
                n %= v;
            }
        }
        if (n > 0) parts.Add(ThreeDigits(n));
        return string.Join(" ", parts);
    }

    /// <summary>Figure by figure: "8080" is "eight zero eight zero".</summary>
    public static string Digits(string s)
    {
        var words = new List<string>();
        foreach (var c in s)
            if (c is >= '0' and <= '9') words.Add(Ones[c - '0']);
        return string.Join(" ", words);
    }

    /// <summary>English cardinal: "2.5" is "two point five", a leading zero switches to figure-by-figure reading.</summary>
    public static string Words(string s)
    {
        var clean = s.Replace(",", "", StringComparison.Ordinal);
        var dot = clean.IndexOf('.');
        if (dot >= 0)
        {
            var intPart = clean[..dot];
            var frac = clean[(dot + 1)..];
            var intWords = intPart.Length == 0 ? "zero" : Words(intPart);
            if (frac.Length == 0) return intWords;
            return intWords + " point " + Digits(frac);
        }
        if (clean.Length > 1 && clean[0] == '0') return Digits(clean);
        if (RustText.TryParseU64(clean, out var n) && RustText.Utf8Len(clean) <= 15) return IntWords(n);
        return Digits(clean);
    }

    [GeneratedRegex(Rx.BL + @"([a-z]+)([24])([a-z]+)" + Rx.BR, Rx.Options)]
    private static partial Regex ReSandwich24();

    /// <summary>"text2text" and "sale4u": a 2 or 4 between lowercase letters reads "two" or "four".</summary>
    public static string ExpandSandwichDigits(string text)
    {
        if (text.IndexOf('2') < 0 && text.IndexOf('4') < 0) return text;
        return ReSandwich24().Replace(text, m =>
        {
            var left = m.V(1);
            if (left is "sin" or "cos" or "tan" or "cot" or "log" or "lg" or "ln" or "lim") return m.Value;
            var d = m.V(2) == "2" ? "two" : "four";
            static string Wrap(string s) => RustText.CharCount(s) == 1 ? "__start_en__" + s + "__end_en__" : s;
            return Wrap(left) + " " + d + " " + Wrap(m.V(3));
        });
    }

    [GeneratedRegex(Rx.BL + @"(\d{1,2}):([0-5]\d)" + Rx.BR, Rx.Options)]
    private static partial Regex ReEnTime();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%", Rx.Options)]
    private static partial Regex ReEnPercent();

    [GeneratedRegex(@"\$\s*(\d+(?:,\d{3})*(?:\.\d+)?)", Rx.Options)]
    private static partial Regex ReEnDollar();

    [GeneratedRegex(@"(?i)" + Rx.BL + @"(\d+(?:\.\d+)?)\s*(tb|gb|mb|kb|ghz|mhz|khz|hz|kg|km|cm|mm|ml|mah|dpi|fps|mph)" + Rx.BR, Rx.Options)]
    private static partial Regex ReEnUnit();

    [GeneratedRegex(Rx.BL + @"(\d{1,4})/(\d{1,4})" + Rx.BR, Rx.Options)]
    private static partial Regex ReEnSlashNum();

    [GeneratedRegex(Rx.BL + @"[A-Za-z0-9]+" + Rx.BR, Rx.Options)]
    private static partial Regex ReEnAlnum();

    [GeneratedRegex(@"[A-Za-z]+|\d+", Rx.Options)]
    internal static partial Regex ReSubTokens();

    [GeneratedRegex(@"\d+(?:,\d{3})*(?:\.\d+)?", Rx.Options)]
    private static partial Regex ReEnNumber();

    [GeneratedRegex(@"(^|[\s(])-(\d)", Rx.Options)]
    private static partial Regex ReEnMinus();

    [GeneratedRegex(@"\+\s*(\d)", Rx.Options)]
    private static partial Regex ReEnPlus();

    [GeneratedRegex(@"([A-Za-z])\.([A-Za-z])", Rx.Options)]
    private static partial Regex ReEnInnerDot();

    private static string UnitName(string unit, bool singular)
    {
        var b = unit switch
        {
            "tb" => "terabyte", "gb" => "gigabyte", "mb" => "megabyte", "kb" => "kilobyte",
            "ghz" => "gigahertz", "mhz" => "megahertz", "khz" => "kilohertz", "hz" => "hertz",
            "kg" => "kilogram", "km" => "kilometer", "cm" => "centimeter", "mm" => "millimeter",
            "ml" => "milliliter", "mah" => "milliamp hour", "dpi" => "d p i", "fps" => "f p s",
            "mph" => "mile per hour",
            _ => unit,
        };
        var noPlural = unit is "hz" or "ghz" or "mhz" or "khz" or "dpi" or "fps" or "mph";
        return singular || noPlural ? b : b + "s";
    }

    private static ulong ParseOrZero(string s) => RustText.TryParseU64(s, out var v) ? v : 0;

    /// <summary>Rewrite the numbers and symbols of a pure-English sentence as English words.</summary>
    public static string EnglishPrenormalize(string text)
    {
        var t = ReEnTime().Replace(text, m =>
        {
            var h = ParseOrZero(m.V(1));
            var min = ParseOrZero(m.V(2));
            if (min == 0) return IntWords(h) + " o'clock";
            if (min < 10) return IntWords(h) + " oh " + IntWords(min);
            return IntWords(h) + " " + IntWords(min);
        });
        t = ReEnPercent().Replace(t, m => Words(m.V(1)) + " percent");
        t = ReEnDollar().Replace(t, m =>
        {
            var num = m.V(1);
            return Words(num) + " " + (num == "1" ? "dollar" : "dollars");
        });
        t = ReEnUnit().Replace(t, m =>
        {
            var num = m.V(1);
            return Words(num) + " " + UnitName(RustText.ToLower(m.V(2)), num == "1");
        });
        t = ReEnSlashNum().Replace(t, m => Words(m.V(1)) + " slash " + Words(m.V(2)));
        t = ReEnAlnum().Replace(t, m =>
        {
            var tok = m.Value;
            var hasDigit = tok.Any(RustText.IsAsciiDigit);
            var hasLetter = tok.Any(RustText.IsAsciiAlpha);
            if (!hasDigit || !hasLetter) return tok;
            var pieces = new List<string>();
            foreach (Match sub in ReSubTokens().Matches(tok))
            {
                var s = sub.Value;
                pieces.Add(s.All(RustText.IsAsciiDigit) ? Words(s) : s);
            }
            return string.Join(" ", pieces);
        });
        t = ReEnMinus().Replace(t, "${1}minus $2");
        t = ReEnPlus().Replace(t, "plus $1");
        t = ReEnNumber().Replace(t, m => Words(m.Value));
        for (var i = 0; i < 3; i++)
        {
            var next = ReEnInnerDot().Replace(t, "$1 dot $2");
            if (next == t) break;
            t = next;
        }
        return t.Replace(" & ", " and ", StringComparison.Ordinal)
            .Replace(" + ", " plus ", StringComparison.Ordinal)
            .Replace(" = ", " equals ", StringComparison.Ordinal);
    }
}
