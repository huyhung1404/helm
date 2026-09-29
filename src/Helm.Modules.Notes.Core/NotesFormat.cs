using System.Globalization;

namespace Helm.Modules.Notes;

/// <summary>Display text for notes, in the current culture.</summary>
public static class NotesFormat
{
    /// <summary>"just now", "5 min ago", "14:30" (today), "Yesterday", "28 Sep" (this year) or "28 Sep 2025".</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (local.Date == today) return local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        return local.Year == today.Year
            ? local.ToString("d MMM", CultureInfo.CurrentCulture)
            : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>"1 word", "1,234 words".</summary>
    public static string Words(int count) => count == 1 ? "1 word" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} words";
}
