using System.Globalization;
using System.Text;
using Helm.Core.Sync;
using Helm.Modules.Wallet;

namespace Helm.Modules.Tracker;

/// <summary>What a calendar day lists: a task due that day, a person whose debt is due, or a repeating task's future day.</summary>
/// <param name="TimeText">"09:30 " before a timed entry (in the calendar's time zone), "" for one due on the day.</param>
public sealed record CalendarEntry(string Title, string Detail, DateTimeOffset? At, bool IsDone, bool IsOverdue, bool IsDebt, bool IsProjected,
    string? ItemId, string? WorkspaceId, string? PersonKey, string TimeText = "");

/// <summary>
/// The Tracker's calendar: every task with a due date, on its day, across all to-do lists (or one), and every person in
/// Wallet's debt book whose balance is due (with all lists). A task that repeats every day also shows on its coming
/// days, until it stops.
/// </summary>
public static class TrackerCalendar
{
    /// <summary>The 6 × 7 days of the month of <paramref name="month"/>, starting on <paramref name="firstDay"/>.</summary>
    public static IReadOnlyList<DateOnly> MonthDays(DateOnly month, DayOfWeek firstDay)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var start = first.AddDays(-(((int)first.DayOfWeek - (int)firstDay + 7) % 7));
        return Enumerable.Range(0, 42).Select(start.AddDays).ToList();
    }

    /// <summary>The 7 days of the week of <paramref name="day"/>, starting on <paramref name="firstDay"/>.</summary>
    public static IReadOnlyList<DateOnly> WeekDays(DateOnly day, DayOfWeek firstDay)
    {
        var start = day.AddDays(-(((int)day.DayOfWeek - (int)firstDay + 7) % 7));
        return Enumerable.Range(0, 7).Select(start.AddDays).ToList();
    }

    /// <summary>The entries of each day from <paramref name="from"/> to <paramref name="to"/> (inclusive), timed ones first by time.</summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyList<CalendarEntry>> Entries(
        TrackerStore store, DateOnly from, DateOnly to, string? workspaceId, TimeZoneInfo zone, DebtBook? debts = null)
    {
        var now = store.Now;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var days = new Dictionary<DateOnly, List<CalendarEntry>>();
        void Add(DateOnly day, CalendarEntry entry)
        {
            if (day < from || day > to) return;
            if (entry.At is { } at) entry = entry with { TimeText = TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.CurrentCulture) + " " };
            if (!days.TryGetValue(day, out var list)) days[day] = list = [];
            list.Add(entry);
        }

        foreach (var (wsId, ws, _) in store.Workspaces())
        {
            if (workspaceId is not null && wsId != workspaceId) continue;
            var items = store.Items(wsId);
            foreach (var (id, item, _) in items)
            {
                if (item.IsSubtask || item.DueMoment(zone) is not { } due) continue;
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(due, zone).DateTime);
                var timed = item.DueAt is not null ? due : (DateTimeOffset?)null;
                Add(day, new CalendarEntry(item.Title, ws.Name, timed, item.IsCompleted, !item.IsCompleted && due < now, false, false, id, wsId, null));
            }
            // A daily task exists only up to today; its coming days are shown from the latest one.
            foreach (var series in items.Where(i => i.Value.IsRepeating).GroupBy(i => i.Value.SeriesId))
            {
                var latest = series.OrderByDescending(i => i.Value.OccurrenceDate ?? DateOnly.MinValue).First().Value;
                if (!latest.RepeatDaily || latest.OccurrenceDate is not { } last) continue;
                var end = latest.RepeatUntil is { } until && until < to ? until : to;
                for (var day = (last > today ? last : today).AddDays(1); day <= end; day = day.AddDays(1))
                {
                    var timed = latest.DueAt is { } at ? at.AddDays(day.DayNumber - last.DayNumber) : (DateTimeOffset?)null;
                    Add(day, new CalendarEntry(latest.Title, ws.Name, timed, false, false, false, true, null, wsId, null));
                }
            }
        }
        if (debts is not null && workspaceId is null)
        {
            foreach (var person in debts.Open().Where(p => !p.IsSettled))
            {
                if (person.Due(zone) is not { } due) continue;
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(due, zone).DateTime);
                var timed = person.HasDueTime ? due : (DateTimeOffset?)null;
                Add(day, new CalendarEntry(person.Name, DebtFormat.Balance(person.Balance), timed, false, due < now, true, false, null, null, person.Key));
            }
        }
        return days.ToDictionary(d => d.Key, d => (IReadOnlyList<CalendarEntry>)d.Value
            .OrderBy(e => e.IsDone)
            .ThenBy(e => e.At ?? DateTimeOffset.MaxValue)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }
}

/// <summary>
/// The open tasks and the debts (Wallet's debt book) with a due date as an iCalendar file (RFC 5545), to import into Google Calendar, Outlook or
/// a phone's calendar. Every event keeps the item's id, so importing again updates the events rather than doubling them.
/// </summary>
public static class TrackerIcs
{
    public static string Build(TrackerStore store, TimeZoneInfo zone, DebtBook? debts = null)
    {
        var now = store.Now;
        var ics = new StringBuilder();
        void Line(string text)
        {
            // Lines are at most 75 octets; longer ones continue on the next line after a space.
            var bytes = Encoding.UTF8.GetBytes(text);
            var start = 0;
            var first = true;
            while (start < bytes.Length)
            {
                var max = first ? 75 : 74;
                var length = Math.Min(max, bytes.Length - start);
                while (length > 0 && start + length < bytes.Length && (bytes[start + length] & 0xC0) == 0x80) length--; // do not split a letter
                ics.Append(first ? "" : " ").Append(Encoding.UTF8.GetString(bytes, start, length)).Append("\r\n");
                start += length;
                first = false;
            }
        }

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//Helm//Tracker//EN");
        Line("CALSCALE:GREGORIAN");
        Line("X-WR-CALNAME:Helm Tracker");
        var stamp = Utc(now);

        foreach (var (wsId, ws, _) in store.Workspaces())
        {
            var items = store.Items(wsId);
            // A repeating task is one event that repeats, from its latest day.
            var latestOfSeries = items.Where(i => i.Value.IsRepeating)
                .GroupBy(i => i.Value.SeriesId!)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.Value.OccurrenceDate ?? DateOnly.MinValue).First().Id);
            foreach (var (id, item, _) in items)
            {
                if (item.IsCompleted || item.IsSubtask || item.DueMoment(zone) is null) continue;
                if (item.SeriesId is { } series && latestOfSeries[series] != id) continue;
                Line("BEGIN:VEVENT");
                Line($"UID:{Escape(item.SeriesId ?? id)}@helm-tracker");
                Line("DTSTAMP:" + stamp);
                Time(item.DueAt, item.DueDate ?? TrackerDue.Day(item.DueAt, zone));
                if (item.RepeatDaily)
                    Line("RRULE:FREQ=DAILY" + (item.RepeatUntil is { } until ? $";UNTIL={until:yyyyMMdd}" : ""));
                Line("SUMMARY:" + Escape(item.Title));
                var description = ws.Name + (item.Notes.Length > 0 ? "\n" + item.Notes : "");
                Line("DESCRIPTION:" + Escape(description));
                Line("CATEGORIES:" + Escape(ws.Name));
                if (item.Priority >= TrackerPriority.High) Line("PRIORITY:" + (item.Priority == TrackerPriority.Urgent ? "1" : "3"));
                Alarm(item.DueAt);
                Line("END:VEVENT");
            }
        }
        // The same UID as when the debt book was the Tracker's, so importing again updates those events.
        foreach (var person in debts?.Open().Where(p => !p.IsSettled) ?? [])
        {
            if (person.Due(zone) is not { } due) continue;
            var timed = person.HasDueTime ? due : (DateTimeOffset?)null;
            var amount = WalletFormat.Money(Math.Abs(person.Balance));
            Line("BEGIN:VEVENT");
            Line($"UID:debt-{Escape(person.Key.Replace(' ', '-'))}@helm-tracker");
            Line("DTSTAMP:" + stamp);
            Time(timed, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(due, zone).DateTime));
            Line("SUMMARY:" + Escape(person.Balance > 0 ? $"{person.Name} pays back {amount}" : $"Pay {person.Name} {amount}"));
            Line("DESCRIPTION:" + Escape("Debts (Wallet)"));
            Line("CATEGORIES:" + Escape("Debts"));
            Alarm(timed);
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return ics.ToString();

        void Time(DateTimeOffset? at, DateOnly? day)
        {
            if (at is { } moment)
            {
                Line("DTSTART:" + Utc(moment));
                Line("DURATION:PT30M");
            }
            else if (day is { } d)
            {
                Line($"DTSTART;VALUE=DATE:{d:yyyyMMdd}");
                Line($"DTEND;VALUE=DATE:{d.AddDays(1):yyyyMMdd}");
            }
        }

        // A reminder 15 minutes before a timed item; items due on a day have none (the calendar's own defaults apply).
        void Alarm(DateTimeOffset? at)
        {
            if (at is null) return;
            Line("BEGIN:VALARM");
            Line("ACTION:DISPLAY");
            Line("DESCRIPTION:Reminder");
            Line("TRIGGER:-PT15M");
            Line("END:VALARM");
        }
    }

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>TEXT escaping: backslash, semicolon, comma and line breaks.</summary>
    internal static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
}
