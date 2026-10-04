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
