using System.Globalization;
using System.Text.RegularExpressions;

namespace Helm.Core.Text;

/// <summary>
/// A day to remember: a birthday or a giỗ every year (solar, or lunar for <see cref="Lunar"/>), or one day only (a car
/// inspection, an insurance or domain renewal) when <see cref="Year"/> is set.
/// </summary>
public sealed record ImportantDay(int Day, int Month, int? Year, bool Lunar, string Label)
{
    /// <summary>The next solar day it falls on, on or after <paramref name="from"/>; null for a one-time day already past.</summary>
    public DateOnly? Next(DateOnly from)
    {
        if (Year is { } year)
            return DateOnly.TryParseExact($"{year:0000}-{Month:00}-{Day:00}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var once)
                && once >= from ? once : null;
        if (Lunar) return LunarCalendar.Next(Day, Month, from);
        for (var y = from.Year; y <= from.Year + 1; y++)
        {
            // 29 February in a year without one: the 28th.
            var day = Math.Min(Day, DateTime.DaysInMonth(y, Month));
            var solar = new DateOnly(y, Month, day);
            if (solar >= from) return solar;
        }
        return null;
    }

    /// <summary>The line it is kept as in the note: "15/03 · Sinh nhật mẹ", "10/07 âm · Giỗ ông", "20/05/2027 · Đăng kiểm xe".</summary>
    public string Line() =>
        (Year is { } y ? $"{Day:00}/{Month:00}/{y:0000}" : $"{Day:00}/{Month:00}") + (Lunar ? " âm" : "") + " · " + Label.Trim();
}

/// <summary>
/// The note "Ngày quan trọng" where Helm keeps the days to remember, one per line (<see cref="ImportantDay.Line"/>):
/// the user can write them by hand too. Lines that are not a day are left alone.
/// </summary>
public static partial class ImportantDays
{
    public const string NoteTitle = "Ngày quan trọng";

    /// <summary>The days of a note's text, in its order.</summary>
    public static IReadOnlyList<ImportantDay> Parse(string text)
    {
        var days = new List<ImportantDay>();
        foreach (var raw in text.Split('\n'))
            if (ParseLine(raw) is { } day) days.Add(day);
        return days;
    }

    public static ImportantDay? ParseLine(string line)
    {
        var m = Line().Match(line);
        if (!m.Success) return null;
        var day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        int? year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) : null;
        var lunar = m.Groups["lunar"].Success;
        if (month is < 1 or > 12 || day < 1 || day > (lunar ? 30 : DateTime.DaysInMonth(year ?? 2024, month))) return null;
        // A lunar day is a yearly one.
        return new ImportantDay(day, month, lunar ? null : year, lunar, m.Groups["label"].Value.Trim());
    }

    /// <summary>
    /// Reads what a tool was given for the day: "15/03", "15/03/2027", "2027-03-15" or "03-15" (month and day).
    /// </summary>
    public static (int Day, int Month, int? Year)? ParseDate(string text)
    {
        text = text.Trim();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
            return (full.Day, full.Month, full.Year);
        var md = MonthDay().Match(text);
        if (md.Success) return Valid(int.Parse(md.Groups["d"].Value, CultureInfo.InvariantCulture), int.Parse(md.Groups["m"].Value, CultureInfo.InvariantCulture), null);
        var dm = DayMonth().Match(text);
        if (!dm.Success) return null;
        int? year = dm.Groups["y"].Success ? int.Parse(dm.Groups["y"].Value, CultureInfo.InvariantCulture) : null;
        return Valid(int.Parse(dm.Groups["d"].Value, CultureInfo.InvariantCulture), int.Parse(dm.Groups["m"].Value, CultureInfo.InvariantCulture), year);
    }

    private static (int, int, int?)? Valid(int day, int month, int? year) =>
        month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year ?? 2024, month) ? (day, month, year) : null;

    [GeneratedRegex(@"^\s*[-*•]?\s*(?<d>\d{1,2})[/.](?<m>\d{1,2})(?:[/.](?<y>\d{4}))?\s*(?<lunar>âm|ÂL|AL|al|âl)?\s*[·:–—-]\s*(?<label>\S.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Line();

    [GeneratedRegex(@"^(?<m>\d{1,2})-(?<d>\d{1,2})$")]
    private static partial Regex MonthDay();

    [GeneratedRegex(@"^(?<d>\d{1,2})[/.](?<m>\d{1,2})(?:[/.](?<y>\d{4}))?$")]
    private static partial Regex DayMonth();
}
