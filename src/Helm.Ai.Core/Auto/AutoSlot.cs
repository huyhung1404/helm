using System.Globalization;

namespace Helm.Ai.Auto;

public enum AutoEvery
{
    Hour,
    Day,
    Week,
    Month,
}

/// <summary>
/// One time a job comes round: every hour, or at its time of day every day, on a weekday or on a day of the month. It
/// may run from that time for <see cref="Window"/> (a PC that was asleep, or had no agent ready, still runs it), once
/// per period.
/// </summary>
public sealed record AutoSlot(string Name, AutoEvery Every, DayOfWeek WeekDay = DayOfWeek.Sunday, int MonthDay = 1)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(4);

    /// <summary>
    /// The period due at local time <paramref name="now"/> (its key: the day, "2026-11-03", or the hour,
    /// "2026-11-03T14"), or null when it is not the slot's time.
    /// </summary>
    public string? PeriodAt(DateTime now, TimeOnly at)
    {
        if (Every == AutoEvery.Hour) return now.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(now);
        // A window that starts late in the evening goes on past midnight.
        foreach (var day in new[] { today, today.AddDays(-1) })
        {
            var start = day.ToDateTime(at);
            if (now >= start && now < start + Window && Falls(day)) return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return null;
    }

    private bool Falls(DateOnly day) => Every switch
    {
        AutoEvery.Week => day.DayOfWeek == WeekDay,
        AutoEvery.Month => day.Day == MonthDay,
        _ => true,
    };
}
