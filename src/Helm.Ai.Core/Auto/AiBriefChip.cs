using System.Globalization;
using Helm.Core.Sync;

namespace Helm.Ai.Auto;

/// <summary>
/// The chip at the top of Ask Helm's card to read the morning brief or the evening review again: the latest of them
/// from today or yesterday, and — when that one is today's — yesterday's last one behind "Hôm qua". Nothing older.
/// </summary>
/// <param name="Yesterday">Yesterday's last brief, when <see cref="Latest"/> is from today; else null.</param>
public sealed record AiBriefChip(SyncedItem<AiBrief> Latest, SyncedItem<AiBrief>? Yesterday)
{
    /// <summary>The briefs the chip shows: the day's two, not the other jobs' (those are read through Ask Helm).</summary>
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) { AiFeatureIds.MorningBrief, AiFeatureIds.EveningReview };

    /// <summary>The chip for these briefs at <paramref name="now"/> in <paramref name="zone"/>; null when there is none to show.</summary>
    public static AiBriefChip? Pick(IEnumerable<SyncedItem<AiBrief>> briefs, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = Day(now, zone);
        var recent = briefs
            .Where(b => Kinds.Contains(b.Value.Kind) && b.Value.CreatedAt <= now && today.DayNumber - Day(b.Value.CreatedAt, zone).DayNumber is 0 or 1)
            .OrderByDescending(b => b.Value.CreatedAt)
            .ToList();
        if (recent.Count == 0) return null;
        var latest = recent[0];
        var yesterday = Day(latest.Value.CreatedAt, zone) == today
            ? recent.FirstOrDefault(b => Day(b.Value.CreatedAt, zone) != today)
            : null;
        return new AiBriefChip(latest, yesterday);
    }

    /// <summary>"Bản tin sáng · 06:45", "Tổng kết tối · Hôm qua 21:30".</summary>
    public static string Label(AiBrief brief, DateTimeOffset now, TimeZoneInfo zone)
    {
        var at = TimeZoneInfo.ConvertTime(brief.CreatedAt, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var when = Day(brief.CreatedAt, zone) == Day(now, zone) ? at : $"Hôm qua {at}";
        var name = brief.Kind == AiFeatureIds.EveningReview ? "Tổng kết tối" : "Bản tin sáng";
        return $"{name} · {when}";
    }

    private static DateOnly Day(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
}
