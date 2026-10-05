using Helm.Modules.Tracker;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>The Tracker calendar (month and week grids, what each day lists) and its .ics export.</summary>
public sealed class TrackerCalendarTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero));
    private readonly TrackerStore _store;
    private readonly DebtBook _debts;

    public TrackerCalendarTests()
    {
        _store = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        _debts = new DebtBook(new MemorySynced<WalletDebt>(), wallet, _time);
    }

    [Fact]
    public void A_month_grid_is_six_weeks_from_the_first_day_of_the_week()
    {
        var days = TrackerCalendar.MonthDays(new DateOnly(2026, 9, 15), DayOfWeek.Monday);
        Assert.Equal(42, days.Count);
        Assert.Equal(new DateOnly(2026, 8, 31), days[0]); // September 2026 starts on a Tuesday
        Assert.All(days, d => Assert.True(d >= days[0]));
        Assert.Equal(DayOfWeek.Monday, days[0].DayOfWeek);

        var week = TrackerCalendar.WeekDays(new DateOnly(2026, 9, 27), DayOfWeek.Monday); // a Sunday
        Assert.Equal(new DateOnly(2026, 9, 21), week[0]);
        Assert.Equal(new DateOnly(2026, 9, 27), week[^1]);
        Assert.Equal(new DateOnly(2026, 9, 27), TrackerCalendar.WeekDays(new DateOnly(2026, 9, 27), DayOfWeek.Sunday)[0]);
    }

    [Fact]
    public void Days_list_due_tasks_timed_first_debts_and_the_coming_days_of_a_daily_task()
    {
        var list = _store.AddWorkspace("Work", WorkspaceKind.Tasks);
        _store.AddItem(list, new TrackerItemDraft("Report", DueDate: new DateOnly(2026, 9, 30)));
        _store.AddItem(list, new TrackerItemDraft("Call", DueAt: new DateTimeOffset(2026, 9, 30, 9, 30, 0, TimeSpan.Zero), DueDate: new DateOnly(2026, 9, 30)));
        var late = _store.AddItem(list, new TrackerItemDraft("Late one", DueDate: new DateOnly(2026, 9, 28)));
        _store.AddItem(list, new TrackerItemDraft("No date"));
        _store.AddItem(list, new TrackerItemDraft("Stretch", RepeatDaily: true, RepeatUntil: new DateOnly(2026, 10, 2)));
        _debts.Add("Nam", 200_000, DebtEntryKind.OwesMe, "lunch", new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero));

        var entries = TrackerCalendar.Entries(_store, new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 4), null, Zone, _debts);

        Assert.Equal(["Call", "Report", "Stretch"], entries[new DateOnly(2026, 9, 30)].Select(e => e.Title));
        Assert.Equal("09:30 ", entries[new DateOnly(2026, 9, 30)][0].TimeText);
        Assert.True(entries[new DateOnly(2026, 9, 28)].Single().IsOverdue);
        Assert.Equal(late, entries[new DateOnly(2026, 9, 28)].Single().ItemId);
        var debt = entries[new DateOnly(2026, 10, 1)].Single(e => e.IsDebt);
        Assert.Equal(("Nam", "nam"), (debt.Title, debt.PersonKey));
        // Today's occurrence exists (no due date, so not shown); the coming days are projected until it ends.
        Assert.True(entries[new DateOnly(2026, 10, 2)].Single().IsProjected);
        Assert.False(entries.ContainsKey(new DateOnly(2026, 10, 3)));
        Assert.DoesNotContain(entries.Values.SelectMany(e => e), e => e.Title == "No date");

        // One list only: its tasks, no debts.
        var onlyList = TrackerCalendar.Entries(_store, new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 4), list, Zone, _debts);
        Assert.DoesNotContain(onlyList.Values.SelectMany(e => e), e => e.IsDebt);
        Assert.Contains(onlyList.Values.SelectMany(e => e), e => e.Title == "Report");
    }

    [Fact]
    public void The_ics_has_one_event_per_open_task_or_debt_and_repeats_a_daily_task()
    {
        var list = _store.AddWorkspace("Việc, nhà", WorkspaceKind.Tasks);
        var report = _store.AddItem(list, new TrackerItemDraft("Report; draft", DueDate: new DateOnly(2026, 9, 30), Notes: "line 1\nline 2"));
        _store.AddItem(list, new TrackerItemDraft("Call", DueAt: new DateTimeOffset(2026, 9, 30, 9, 30, 0, TimeSpan.Zero), Priority: TrackerPriority.Urgent));
        var done = _store.AddItem(list, new TrackerItemDraft("Done already", DueDate: new DateOnly(2026, 9, 29)));
        _store.Complete(done);
        _store.AddItem(list, new TrackerItemDraft("Stretch", DueAt: new DateTimeOffset(2026, 9, 29, 6, 0, 0, TimeSpan.Zero), RepeatDaily: true));
        _debts.Add("Nam", 200_000, DebtEntryKind.OwesMe, "lunch", new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero));
        _debts.Add("Lan", 50_000, DebtEntryKind.IOwe);

        var ics = TrackerIcs.Build(_store, Zone, _debts);

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Equal(4, CountOf(ics, "BEGIN:VEVENT"));
        Assert.Contains($"UID:{report}@helm-tracker", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20260930\r\nDTEND;VALUE=DATE:20261001", ics);
        Assert.Contains("SUMMARY:Report\\; draft", ics);
        Assert.Contains("DESCRIPTION:Việc\\, nhà\\nline 1\\nline 2", ics);
        Assert.Contains("DTSTART:20260930T093000Z\r\nDURATION:PT30M", ics);
        Assert.Contains("PRIORITY:1", ics);
        Assert.Contains("TRIGGER:-PT15M", ics);
        Assert.Contains("RRULE:FREQ=DAILY", ics);
        Assert.Matches(@"SUMMARY:Nam pays back 200.000", ics.Replace(@"\,", ","));
        Assert.Contains("UID:debt-nam@helm-tracker", ics); // the same as when the debt book was the Tracker's
        Assert.DoesNotContain("Done already", ics);
        Assert.DoesNotContain("Lan", ics); // no due date
        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
    }

    [Fact]
    public void Long_lines_are_folded_without_splitting_a_letter()
    {
        var list = _store.AddWorkspace("Work", WorkspaceKind.Tasks);
        _store.AddItem(list, new TrackerItemDraft(string.Concat(Enumerable.Repeat("Đặt vé máy bay ", 12)), DueDate: new DateOnly(2026, 9, 30)));
        var ics = TrackerIcs.Build(_store, Zone);
        var summary = string.Concat(ics.Split("\r\n").SkipWhile(l => !l.StartsWith("SUMMARY:")).TakeWhile((l, i) => i == 0 || l.StartsWith(' ')).Select((l, i) => i == 0 ? l : l[1..]));
        Assert.Equal("SUMMARY:" + string.Concat(Enumerable.Repeat("Đặt vé máy bay ", 12)).TrimEnd(), summary);
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;
}
