using System.Globalization;

namespace Helm.Ai.Auto;

/// <param name="Name">The job as the user knows it (his own words, Vietnamese).</param>
/// <param name="When">When it runs, for Ask Helm to tell.</param>
public sealed record AutoJobInfo(string Feature, string Name, bool OnByDefault, TimeOnly DefaultAt, string When);

/// <summary>
/// The jobs a Helm PC runs by itself (<see cref="AutoJobScheduler"/>): on or off and their time are each feature's
/// choice in the synced ai-features settings. Their calls go to this PC's agents only, never the paid API, unless the
/// user sets the feature's route to allow it.
/// </summary>
public static class AutoJobCatalog
{
    public static IReadOnlyList<AutoJobInfo> All { get; } =
    [
        new(AiFeatureIds.MorningBrief, "Bản tin buổi sáng", true, new TimeOnly(7, 0), "every morning"),
        new(AiFeatureIds.EveningReview, "Tổng kết buổi tối", true, new TimeOnly(21, 30), "every evening"),
        new(AiFeatureIds.HabitCoach, "Huấn luyện thói quen", false, new TimeOnly(19, 0), "on Sundays, and on the evening a habit is skipped a second day in a row"),
        new(AiFeatureIds.SpendingDigest, "Tóm tắt chi tiêu tuần, tháng", true, new TimeOnly(20, 0), "on Sundays (the week) and on the 1st (the month before)"),
        new(AiFeatureIds.ImportantDays, "Ngày quan trọng", false, new TimeOnly(8, 0), "every morning: 3 days before a day to remember, and on the day"),
        new(AiFeatureIds.NoteTitles, "Tự đặt tiêu đề và tag", false, new TimeOnly(0, 0), "every hour, for new notes without a title"),
        new(AiFeatureIds.ScratchTidy, "Dọn Scratch", false, new TimeOnly(10, 0), "on Saturdays (suggestions only, nothing is moved)"),
    ];

    public static AutoJobInfo? Find(string feature) => All.FirstOrDefault(j => j.Feature == feature);

    /// <summary>A feature's choice before the user made one: an automatic job is on or off as listed, and runs on a PC only.</summary>
    public static AiFeatureChoice DefaultChoice(string feature) =>
        Find(feature) is { } job ? new AiFeatureChoice { Enabled = job.OnByDefault, Route = AiRoutePolicy.PcOnly } : new AiFeatureChoice();

    /// <summary>The job's time of day: the user's ("07:00"), else its own.</summary>
    public static TimeOnly TimeOf(AiFeatureChoice choice, AutoJobInfo job) =>
        ParseTime(choice.At) ?? job.DefaultAt;

    public static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
}
