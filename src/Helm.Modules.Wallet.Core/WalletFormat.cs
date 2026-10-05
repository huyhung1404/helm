using System.Globalization;

namespace Helm.Modules.Wallet;

/// <summary>Text shown for amounts and dates (the same on both apps).</summary>
public static class WalletFormat
{
    public const string Currency = "₫";

    /// <summary>"1,500,000 ₫" (by culture; no decimals unless there are any).</summary>
    public static string Money(decimal amount, CultureInfo? culture = null) =>
        $"{Math.Abs(amount).ToString("#,0.##", culture ?? CultureInfo.CurrentCulture)} {Currency}";

    /// <summary>"−150,000 ₫" (money out), "+5,000,000 ₫" (money in).</summary>
    public static string Signed(decimal amount, CultureInfo? culture = null) =>
        (amount > 0 ? "+" : amount < 0 ? "−" : "") + Money(amount, culture);

    /// <summary>
    /// Thousands separators while an amount is typed ("1000000" becomes "1,000,000" or "1.000.000" by culture), keeping
    /// the caret after the same digit. Left as typed: text with anything but digits and separators in it ("150k", "2tr"),
    /// and a '.' or ',' the person typed themselves (on the way to "1.5tr"), which is any separator added by this edit or
    /// kept from text that was not grouped before it. <paramref name="previous"/> is the text before this edit.
    /// </summary>
    public static (string Text, int Caret) GroupDigits(string text, int caret, string previous, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (text.Length == 0 || !text.All(c => char.IsAsciiDigit(c) || IsSeparator(c))) return (text, caret);
        var separators = text.Count(IsSeparator);
        if (separators > 0 && (separators > previous.Count(IsSeparator) || Grouped(previous, culture) != previous)) return (text, caret);

        var raw = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (raw.Length == 0) return ("", 0);
        var digits = raw.TrimStart('0');
        if (digits.Length == 0) digits = "0";
        // Leading zeros are dropped; they came before the caret.
        var digitsBefore = Math.Max(0, text.Take(Math.Clamp(caret, 0, text.Length)).Count(char.IsAsciiDigit) - (raw.Length - digits.Length));
        var grouped = decimal.Parse(digits, CultureInfo.InvariantCulture).ToString("#,0", culture);
        var newCaret = 0;
        for (var seen = 0; newCaret < grouped.Length && seen < digitsBefore; newCaret++)
            if (char.IsAsciiDigit(grouped[newCaret])) seen++;
        return (grouped, newCaret);
    }

    private static bool IsSeparator(char c) => c is '.' or ',' or ' ' or '\u00a0' or '\u202f';

    /// <summary>The text as <see cref="GroupDigits"/> would show it ("" stays ""); anything else comes back unchanged.</summary>
    private static string Grouped(string text, CultureInfo culture)
    {
        if (text.Length == 0 || !text.All(c => char.IsAsciiDigit(c) || IsSeparator(c)) || !text.Any(char.IsAsciiDigit)) return text;
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
        return decimal.Parse(digits.Length == 0 ? "0" : digits, CultureInfo.InvariantCulture).ToString("#,0", culture);
    }

    /// <summary>"1.2M ₫", "350K ₫", "900 ₫": short amounts for the widget and the chips.</summary>
    public static string Short(decimal amount, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var a = Math.Abs(amount);
        if (a >= 1_000_000_000) return $"{(a / 1_000_000_000).ToString("0.#", culture)}B {Currency}";
        if (a >= 1_000_000) return $"{(a / 1_000_000).ToString("0.#", culture)}M {Currency}";
        if (a >= 10_000) return $"{(a / 1_000).ToString("0", culture)}K {Currency}";
        return Money(a, culture);
    }

    /// <summary>"October 2026".</summary>
    public static string Month(DateOnly month, CultureInfo? culture = null) =>
        month.ToString("MMMM yyyy", culture ?? CultureInfo.CurrentCulture);

    /// <summary>"Today", "Yesterday", "Mon 28 Sep", "28 Sep 2025".</summary>
    public static string Day(DateOnly day, DateOnly today, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year ? day.ToString("ddd d MMM", culture) : day.ToString("d MMM yyyy", culture);
    }

    /// <summary>"14:30" (culture short time).</summary>
    public static string Time(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("t", CultureInfo.CurrentCulture);

    /// <summary>"1 transaction", "3 transactions".</summary>
    public static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    /// <summary>The local day of an instant.</summary>
    public static DateOnly LocalDay(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>The first day of the local month of an instant.</summary>
    public static DateOnly MonthOf(DateTimeOffset at, TimeZoneInfo zone)
    {
        var day = LocalDay(at, zone);
        return new DateOnly(day.Year, day.Month, 1);
    }
}
