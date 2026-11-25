using Helm.Core.Today;
using Helm.Modules.Habits;
using Helm.Modules.Tracker;

namespace Helm.Tests;

/// <summary>What Tracker and Habits give Home's "today".</summary>
public sealed class TodaySourcesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 25);

    private readonly ManualTime _time = new(T0);

    private (TrackerStore Store, TrackerTodaySource Source, string Workspace) Tracker()
    {
        var store = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
        return (store, new TrackerTodaySource(store, TimeZoneInfo.Utc), store.AddWorkspace("Home"));
    }

    [Fact]
    public void Tracker_with_nothing_due_has_no_card()
    {
        var (store, source, ws) = Tracker();
        store.AddItem(ws, new TrackerItemDraft("Someday"));
        store.AddItem(ws, new TrackerItemDraft("Next week", DueDate: Today.AddDays(7)));

        Assert.Null(source.Build());
    }

    [Fact]
    public void Tracker_lists_today_and_overdue_with_overdue_counted()
    {
        var (store, source, ws) = Tracker();
        store.AddItem(ws, new TrackerItemDraft("Today", DueDate: Today));
        store.AddItem(ws, new TrackerItemDraft("Late", DueDate: Today.AddDays(-2)));
        store.AddItem(ws, new TrackerItemDraft("Later", DueDate: Today.AddDays(3)));

        var list = source.Build()!;

        Assert.Equal("2 open today · 1 overdue", list.Headline);
        Assert.Equal(90, list.Urgency);
        Assert.Equal(["Late", "Today"], list.Entries.Select(e => e.Text));
        Assert.True(list.Entries[0].IsLate);
        Assert.False(list.Entries[1].IsLate);
        Assert.All(list.Entries, e => Assert.True(e.CanTick));
    }

    [Fact]
    public void Tracker_tick_completes_and_untick_reopens()
    {
        var (store, source, ws) = Tracker();
        var id = store.AddItem(ws, new TrackerItemDraft("Pay", DueDate: Today));

        source.Tick(id, true);
        Assert.Null(source.Build());

        source.Tick(id, false);
        Assert.Single(source.Build()!.Entries);
    }

    [Fact]
    public void Tracker_shows_a_few_but_counts_all()
    {
        var (store, source, ws) = Tracker();
        for (var i = 0; i < 8; i++) store.AddItem(ws, new TrackerItemDraft("T" + i, DueDate: Today));

        var list = source.Build()!;

        Assert.Equal(TrackerTodaySource.MaxEntries, list.Entries.Count);
        Assert.Equal("8 open today", list.Headline);
    }

    [Fact]
    public void Tracker_source_changes_with_the_store()
    {
        var (store, source, ws) = Tracker();
        var raised = 0;
        source.Changed += (_, _) => raised++;

        store.AddItem(ws, new TrackerItemDraft("x", DueDate: Today));

        Assert.True(raised > 0);
    }

    private (HabitsStore Store, HabitsTodaySource Source) Habits()
    {
        var store = new HabitsStore(new MemorySynced<Habit>(), new MemorySynced<HabitCheck>(), new MemorySynced<HabitLapse>(), _time, TimeZoneInfo.Utc);
        return (store, new HabitsTodaySource(store));
    }

    [Fact]
    public void Habits_card_says_how_many_are_done_and_ticks_in_place()
    {
        var (store, source) = Habits();
        var read = store.Add("Read")!;
        store.Add("Run");

        Assert.Equal("0 of 2 done", source.Build()!.Headline);

        source.Tick(read, true);
        var list = source.Build()!;
        Assert.Equal("1 of 2 done", list.Headline);
        Assert.Equal(["Run", "Read"], list.Entries.Select(e => e.Text));
        Assert.True(list.Entries[1].IsDone);

        source.Tick(read, false);
        Assert.Equal("0 of 2 done", source.Build()!.Headline);
        Assert.False(store.IsDone(read, Today));
    }

    [Fact]
    public void Habits_has_no_card_when_all_are_done_or_there_are_none()
    {
        var (store, source) = Habits();
        Assert.Null(source.Build());

        source.Tick(store.Add("Read")!, true);
        Assert.Null(source.Build());
    }

    [Fact]
    public void Habits_to_quit_stay_in_Habits()
    {
        var (store, source) = Habits();
        store.Add("Smoking", HabitKind.Quit);

        Assert.Null(source.Build());
    }
}
