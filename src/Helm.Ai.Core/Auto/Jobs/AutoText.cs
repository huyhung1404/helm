using System.Globalization;

namespace Helm.Ai.Auto.Jobs;

/// <summary>Days as the briefs write them (Vietnamese), and as the tools take them.</summary>
internal static class AutoText
{
    private static readonly string[] Weekdays = ["Chủ nhật", "Thứ Hai", "Thứ Ba", "Thứ Tư", "Thứ Năm", "Thứ Sáu", "Thứ Bảy"];

    /// <summary>"Thứ Ba 03/11".</summary>
    public static string Day(DateOnly day) => $"{Weekdays[(int)day.DayOfWeek]} {day.ToString("dd/MM", CultureInfo.InvariantCulture)}";

    /// <summary>"2026-11-03", for the tools and the prompts.</summary>
    public static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The local day a tool's timestamp falls on.</summary>
    public static DateOnly? LocalDay(string? timestamp) =>
        DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).DateTime)
            : null;
}
