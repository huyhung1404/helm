using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Helm.Modules.Wallet;

/// <summary>Text helpers for Vietnamese bank messages: accent folding that keeps positions, amounts, and word sets.</summary>
public static class WalletText
{
    // Words every bank description has, which say nothing about where the money went.
    private static readonly HashSet<string> s_stopWords = new(StringComparer.Ordinal)
    {
        "chuyen", "tien", "ck", "ct", "tu", "den", "cho", "qua", "tai", "thanh", "toan", "tt", "gd", "giao", "dich",
        "noi", "dung", "nd", "vnd", "ma", "so", "tk", "ref", "ibft", "fast", "ft", "the", "va", "and", "to", "from",
        "payment", "transfer", "via", "app", "mobile", "banking", "ngay", "luc", "qr", "pay", "trace", "ib", "mb",
        "vcb", "tcb", "acb", "nhan", "di", "khoan", "toi", "cua", "voi", "le", "phi",
    };

    /// <summary>
    /// Lower case without Vietnamese accents ("Số dư" → "so du", "Đ" → "d"), one character for each character of
    /// <paramref name="text"/>, so a match found in the folded text is at the same place in the original.
    /// Pass NFC text (see <see cref="Clean"/>).
    /// </summary>
    public static string Fold(string text)
    {
        var chars = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            chars[i] = c switch
            {
                'đ' or 'Đ' => 'd',
                '−' or '–' => '-', // minus sign, en dash
                < (char)128 => char.ToLowerInvariant(c),
                _ when char.IsWhiteSpace(c) => ' ',
                _ => char.ToLowerInvariant(c.ToString().Normalize(NormalizationForm.FormD)[0]),
            };
        }
        return new string(chars);
    }

    /// <summary>NFC, with Windows line ends and no-break spaces made plain.</summary>
    public static string Clean(string? text) =>
        (text ?? "").Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ').Replace(' ', ' ').Trim();

    /// <summary>
    /// A number as banks write it: "1,500,000", "1.500.000", "1500000", "1,234.50". A last group of one or two digits
    /// is decimals; groups of three are thousands.
    /// </summary>
    public static bool TryParseNumber(string text, out decimal value)
    {
        value = 0;
        var s = text.Trim();
        if (s.Length == 0) return false;
        var last = s.LastIndexOfAny(['.', ',']);
        var intPart = s;
        var fraction = "";
        if (last >= 0 && s.Length - last - 1 is 1 or 2)
        {
            intPart = s[..last];
            fraction = s[(last + 1)..];
        }
        var digits = new string(intPart.Where(c => c is not ('.' or ',')).ToArray());
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit) || !fraction.All(char.IsAsciiDigit)) return false;
        return decimal.TryParse(fraction.Length == 0 ? digits : digits + "." + fraction, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Reads an amount as people type it: "50000", "50,000", "50.000", "50k", "1.5tr", "2m", "2tr5" (2,500,000).
    /// A single '.' or ',' followed by 1–2 digits is a decimal point; otherwise separators group thousands.
    /// </summary>
    public static bool TryParseAmount(string? text, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = Fold(text.Trim()).Replace(" ", "").Replace("₫", "").Replace("vnd", "");
        if (s.EndsWith('d') && s.Length > 1 && char.IsAsciiDigit(s[^2])) s = s[..^1];

        decimal multiplier = 1;
        var trIndex = s.IndexOf("tr", StringComparison.Ordinal);
        if (trIndex > 0 && s[(trIndex + 2)..].All(char.IsAsciiDigit) && s.Length - trIndex - 2 <= 3)
        {
            // "2tr5" is 2.5 million, "2tr" two million.
            var rest = s[(trIndex + 2)..];
            s = rest.Length == 0 ? s[..trIndex] : s[..trIndex] + "." + rest;
            multiplier = 1_000_000;
        }
        else if (s.EndsWith('m')) { multiplier = 1_000_000; s = s[..^1]; }
        else if (s.EndsWith('k')) { multiplier = 1_000; s = s[..^1]; }
        if (s.Length == 0) return false;

        var separators = s.Count(c => c is '.' or ',');
        var last = s.LastIndexOfAny(['.', ',']);
        string normalized;
        if (separators == 0)
            normalized = s;
        else if (separators == 1 && (s.Length - last - 1 is 1 or 2 || multiplier != 1))
            normalized = s.Remove(last, 1).Insert(last, "."); // decimal point ("1.5k", "12,50")
        else if (s.Length - last - 1 is 1 or 2 && s.Count(c => c == s[last]) == 1)
            normalized = new string(s[..last].Where(char.IsAsciiDigit).ToArray()) + "." + s[(last + 1)..]; // "1,234.56"
        else
            normalized = new string(s.Where(char.IsAsciiDigit).ToArray()); // thousands groups only

        if (normalized.Length == 0 || !normalized.All(c => char.IsAsciiDigit(c) || c == '.') || normalized.Count(c => c == '.') > 1) return false;
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return false;
        amount = decimal.Round(value * multiplier, 0);
        return amount > 0;
    }

    /// <summary>
    /// A time of day as typed: "14:30", "1430", "9", "9h", "9h30", "2:05 PM". Empty text is no time (true, null).
    /// </summary>
    public static bool TryParseTime(string? text, out TimeSpan? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var s = text.Trim().ToLowerInvariant().Replace('h', ':').Replace('.', ':');
        if (s.EndsWith(':')) s = s[..^1];
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed)
            || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out parsed))
        {
            time = new TimeSpan(parsed.Hour, parsed.Minute, 0);
            return true;
        }
        var digits = new string(s.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is < 1 or > 4 || digits.Length != s.Replace(":", "").Length) return false;
        var hours = int.Parse(digits.Length <= 2 ? digits : digits[..^2], CultureInfo.InvariantCulture);
        var minutes = digits.Length <= 2 ? 0 : int.Parse(digits[^2..], CultureInfo.InvariantCulture);
        if (hours > 23 || minutes > 59) return false;
        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    /// <summary>"14:30" (24-hour, so it reads the same as it is typed).</summary>
    public static string FormatTime(TimeSpan time) => $"{(int)time.TotalHours % 24:00}:{time.Minutes:00}";

    /// <summary>
    /// The words of a description that tell transactions apart ("THANH TOAN QR GRAB 0912" → grab): folded, without
    /// numbers, references and the words every bank adds.
    /// </summary>
    public static IReadOnlySet<string> Words(string? text)
    {
        // The pages compare every transaction with the others on each refresh: remember the sets.
        var key = text ?? "";
        if (s_words.TryGetValue(key, out var cached)) return cached;
        var words = ComputeWords(key);
        if (s_words.Count > 10_000) s_words.Clear();
        s_words[key] = words;
        return words;
    }

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> s_words = new(StringComparer.Ordinal);

    private static HashSet<string> ComputeWords(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var folded = Fold(Clean(text));
        var start = -1;
        for (var i = 0; i <= folded.Length; i++)
        {
            var isWord = i < folded.Length && char.IsLetterOrDigit(folded[i]);
            if (isWord && start < 0) start = i;
            if (isWord || start < 0) continue;
            var word = folded[start..i];
            start = -1;
            if (word.Length >= 2 && !word.Any(char.IsDigit) && !s_stopWords.Contains(word)) words.Add(word);
        }
        return words;
    }

    /// <summary>How alike two word sets are (0–1, Jaccard); two empty sets are not alike.</summary>
    public static double Similarity(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count(b.Contains);
        return shared / (double)(a.Count + b.Count - shared);
    }
}
