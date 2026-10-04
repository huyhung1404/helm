using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Helm.Core.Text;

/// <summary>Which way a day without a date leans: back (a payment made) or ahead (a due date).</summary>
public enum DateLean
{
    Past,
    Future,
}

/// <summary>
/// The date and time pickers' text, the same on both apps: reads what people type, in English or Vietnamese, with or
/// without accents ("hôm qua 18h45", "mai 9h", "thứ 6", "3/10 14:30", "3h chiều", "now"), and writes a value back
/// as a short phrase ("Today, 14:30", "Yesterday", "Sat 3 Oct, 09:00") or in full for the readout under the box.
/// </summary>
public static partial class DateTimeText
{
    // Relative days, longest first so "hom kia" is not read as "kia".
    private static readonly (string Words, int Days)[] s_relative =
    [
        ("day after tomorrow", 2), ("ngay kia", 2), ("ngay mot", 2), ("hom kia", -2), ("hom qua", -1), ("hom nay", 0), ("ngay mai", 1),
        ("yesterday", -1), ("today", 0), ("tomorrow", 1), ("tmr", 1), ("mot", 2), ("mai", 1), ("qua", -1), ("nay", 0),
    ];

    private static readonly (string Words, DayOfWeek Day)[] s_weekdays =
    [
        ("chu nhat", DayOfWeek.Sunday), ("thu hai", DayOfWeek.Monday), ("thu ba", DayOfWeek.Tuesday), ("thu tu", DayOfWeek.Wednesday),
        ("thu nam", DayOfWeek.Thursday), ("thu sau", DayOfWeek.Friday), ("thu bay", DayOfWeek.Saturday),
        ("thu 2", DayOfWeek.Monday), ("thu 3", DayOfWeek.Tuesday), ("thu 4", DayOfWeek.Wednesday), ("thu 5", DayOfWeek.Thursday),
        ("thu 6", DayOfWeek.Friday), ("thu 7", DayOfWeek.Saturday), ("cn", DayOfWeek.Sunday),
        ("t2", DayOfWeek.Monday), ("t3", DayOfWeek.Tuesday), ("t4", DayOfWeek.Wednesday), ("t5", DayOfWeek.Thursday),
        ("t6", DayOfWeek.Friday), ("t7", DayOfWeek.Saturday),
        ("monday", DayOfWeek.Monday), ("tuesday", DayOfWeek.Tuesday), ("wednesday", DayOfWeek.Wednesday), ("thursday", DayOfWeek.Thursday),
        ("friday", DayOfWeek.Friday), ("saturday", DayOfWeek.Saturday), ("sunday", DayOfWeek.Sunday),
        ("mon", DayOfWeek.Monday), ("tue", DayOfWeek.Tuesday), ("wed", DayOfWeek.Wednesday), ("thu", DayOfWeek.Thursday),
        ("fri", DayOfWeek.Friday), ("sat", DayOfWeek.Saturday), ("sun", DayOfWeek.Sunday),
    ];

    [GeneratedRegex(@"\b(\d{4})-(\d{1,2})-(\d{1,2})\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b(\d{1,2})[/.\-](\d{1,2})(?:[/.\-](\d{2,4}))?\b")]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"\b(\d+)\s*(?:ngay truoc|days? ago)\b")]
    private static partial Regex DaysAgo();

    [GeneratedRegex(@"\b(?:(\d+)\s*ngay nua|in\s*(\d+)\s*days?)\b")]
    private static partial Regex DaysAhead();

    // "14:30", "14h30", "14h", "14g30", "14 gio 30", "2pm", "2:05 pm"
    [GeneratedRegex(@"\b([01]?\d|2[0-3])\s*(?::|h|g|gio)\s*([0-5]\d)?(?:\s*(?:phut|p))?\b|\b([01]?\d|2[0-3])\s*(?=am\b|pm\b)")]
    private static partial Regex Time();

    [GeneratedRegex(@"\b([01]\d|2[0-3])([0-5]\d)\b")]
    private static partial Regex CompactTime();

    [GeneratedRegex(@"\b(pm|chieu|toi|ch)\b")]
    private static partial Regex Afternoon();

    [GeneratedRegex(@"\b(am|sang|sa)\b")]
    private static partial Regex Morning();

    [GeneratedRegex(@"\b(now|bay gio|luc nay|ngay bay gio)\b")]
    private static partial Regex Now();

    [GeneratedRegex(@"[\s,.·\-]+")]
    private static partial Regex Separators();

    /// <summary>Lower case without Vietnamese accents ("Hôm qua" → "hom qua").</summary>
    public static string Fold(string text)
    {
        var d = text.Replace('đ', 'd').Replace('Đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Reads a typed day and/or time. A day without a time leaves <paramref name="time"/> null (the caller keeps the
    /// time it had); a time without a day is today. Weekday names lean the way of <paramref name="lean"/> (the last
    /// Friday, or the next one). Empty text is valid and gives neither.
    /// </summary>
    /// <returns>False when some of the text could not be read.</returns>
    public static bool TryParse(string? text, DateTime now, DateLean lean, out DateOnly? date, out TimeSpan? time)
    {
        date = null;
        time = null;
        var s = " " + Fold(text ?? "").Trim() + " ";
        if (s.Trim().Length == 0) return true;
        var today = DateOnly.FromDateTime(now);

        if (Now().Match(s) is { Success: true } n)
        {
            date = today;
            time = new TimeSpan(now.Hour, now.Minute, 0);
            s = Cut(s, n);
        }

        if (IsoDate().Match(s) is { Success: true } iso && TryDate(int.Parse(iso.Groups[1].Value), int.Parse(iso.Groups[2].Value), int.Parse(iso.Groups[3].Value), out var isoDate))
        {
            date = isoDate;
            s = Cut(s, iso);
        }
        else if (DayMonth().Match(s) is { Success: true } dm)
        {
            var year = dm.Groups[3].Success ? int.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture) : now.Year;
            if (year < 100) year += 2000;
            if (!TryDate(year, int.Parse(dm.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture), out var d)) return false;
            // "3/1" typed in December without a year means next January when looking ahead, and the reverse.
            if (!dm.Groups[3].Success)
            {
                if (lean == DateLean.Future && d < today.AddMonths(-6)) d = d.AddYears(1);
                if (lean == DateLean.Past && d > today.AddMonths(6)) d = d.AddYears(-1);
            }
            date = d;
            s = Cut(s, dm);
        }

        if (DaysAgo().Match(s) is { Success: true } ago)
        {
            date = today.AddDays(-int.Parse(ago.Groups[1].Value, CultureInfo.InvariantCulture));
            s = Cut(s, ago);
        }
        else if (DaysAhead().Match(s) is { Success: true } ahead)
        {
            var value = ahead.Groups[1].Success ? ahead.Groups[1].Value : ahead.Groups[2].Value;
            date = today.AddDays(int.Parse(value, CultureInfo.InvariantCulture));
            s = Cut(s, ahead);
        }

        foreach (var (words, days) in s_relative)
        {
            var at = Find(s, words);
            if (at < 0) continue;
            date = today.AddDays(days);
            s = s.Remove(at, words.Length).Insert(at, " ");
            break;
        }

        foreach (var (words, day) in s_weekdays)
        {
            var at = Find(s, words);
            if (at < 0) continue;
            var diff = ((int)day - (int)today.DayOfWeek + 7) % 7;
            date = lean == DateLean.Future ? today.AddDays(diff) : today.AddDays(diff == 0 ? 0 : diff - 7);
            s = s.Remove(at, words.Length).Insert(at, " ");
            break;
        }

        if (time is null)
        {
            if (Time().Match(s) is { Success: true } t)
            {
                var hour = int.Parse(t.Groups[1].Success ? t.Groups[1].Value : t.Groups[3].Value, CultureInfo.InvariantCulture);
                var minute = t.Groups[2].Success ? int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                s = Cut(s, t);
                if (Afternoon().Match(s) is { Success: true } pm)
                {
                    if (hour < 12) hour += 12;
                    s = Cut(s, pm);
                }
                else if (Morning().Match(s) is { Success: true } am)
                {
                    if (hour == 12) hour = 0;
                    s = Cut(s, am);
                }
                time = new TimeSpan(hour, minute, 0);
            }
            else if (CompactTime().Match(s) is { Success: true } c)
            {
                time = new TimeSpan(int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture), 0);
                s = Cut(s, c);
            }
        }

        // Anything left that is not a separator was not understood.
        if (Separators().Replace(s, "").Length > 0)
        {
            date = null;
            time = null;
            return false;
        }
        if (date is null && time is not null) date = today;
        return date is not null;
    }

    private static string Cut(string s, Match m) => s.Remove(m.Index, m.Length).Insert(m.Index, " ");

    /// <summary>A whole word or phrase in <paramref name="s"/> (space-padded), or -1.</summary>
    private static int Find(string s, string words)
    {
        var at = 0;
        while ((at = s.IndexOf(words, at, StringComparison.Ordinal)) >= 0)
        {
            var before = at == 0 ? ' ' : s[at - 1];
            var after = at + words.Length >= s.Length ? ' ' : s[at + words.Length];
            if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after)) return at;
            at += words.Length;
        }
        return -1;
    }

    private static bool TryDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1900 or > 2200 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        date = new DateOnly(year, month, day);
        return true;
    }

    /// <summary>"Today, 14:30", "Yesterday", "Tomorrow, 09:00", "Sat 3 Oct", "3 Oct 2025, 08:15"; empty for no date.</summary>
    public static string Format(DateOnly? date, TimeSpan? time, DateOnly today, CultureInfo? culture = null)
    {
        if (date is not { } d) return "";
        culture ??= CultureInfo.CurrentCulture;
        var day = (d.DayNumber - today.DayNumber) switch
        {
            0 => "Today",
            -1 => "Yesterday",
            1 => "Tomorrow",
            _ => d.Year == today.Year ? d.ToString("ddd d MMM", culture) : d.ToString("d MMM yyyy", culture),
        };
        return time is { } t ? $"{day}, {Clock(t)}" : day;
    }

    /// <summary>"Saturday 3 October 2026 · 18:45": the readout under the box while typing.</summary>
    public static string Describe(DateOnly date, TimeSpan? time, CultureInfo? culture = null)
    {
        var text = date.ToString("dddd d MMMM yyyy", culture ?? CultureInfo.CurrentCulture);
        return time is { } t ? $"{text} · {Clock(t)}" : text;
    }

    /// <summary>"09:05": 24-hour, as it is typed.</summary>
    public static string Clock(TimeSpan time) => $"{time.Hours:00}:{time.Minutes:00}";
}
