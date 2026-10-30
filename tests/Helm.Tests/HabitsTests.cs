using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Habits;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public sealed class HabitsTests : IDisposable
{
    // A Friday.
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 30);

    private readonly ManualTime _time = new(T0);
    private readonly MemorySynced<Habit> _habits = new();
    private readonly MemorySynced<HabitCheck> _checks = new();
    private readonly MemorySynced<HabitLapse> _lapses = new();
    private readonly HabitsStore _store;
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public HabitsTests()
    {
        _store = new HabitsStore(_habits, _checks, _lapses, _time, TimeZoneInfo.Utc);
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    /// <summary>A habit created on <paramref name="start"/> (the clock stays at <see cref="T0"/>).</summary>
    private string NewHabit(string title, DateOnly start)
    {
        _time.Now = new DateTimeOffset(start.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero);
        var id = _store.Add(title)!;
        _time.Now = T0;
        return id;
    }

    private void Done(string id, params DateOnly[] days)
    {
        foreach (var d in days) Assert.Equal(HabitCheckResult.Checked, _store.Check(id, d));
    }

    private static DateOnly D(int month, int day, int year = 2026) => new(year, month, day);

    private static HashSet<DateOnly> Days(params DateOnly[] days) => [.. days];

    // ---- Store ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_day_is_one_record_on_every_device_and_only_tracked_days_can_be_ticked()
    {
        var id = NewHabit("Read", D(10, 20));

        Assert.Equal(HabitCheckResult.Checked, _store.Check(id, D(10, 25)));
        Assert.Equal(HabitCheckResult.AlreadyDone, _store.Check(id, D(10, 25)));
        Assert.Single(_checks.All());
        Assert.Equal(HabitsStore.CheckId(id, D(10, 25)), _checks.All()[0].Id);

        Assert.Equal(HabitCheckResult.NotTracked, _store.Check(id, D(10, 19)));
        Assert.Equal(HabitCheckResult.NotTracked, _store.Check(id, D(10, 31)));
        Assert.Equal(HabitCheckResult.NotFound, _store.Check("nope", Today));

        Assert.True(_store.Uncheck(id, D(10, 25)));
        Assert.False(_store.Uncheck(id, D(10, 25)));
        Assert.Empty(_checks.All());
    }

    [Fact]
    public void A_habit_starts_today_and_new_habits_go_last()
    {
        var a = _store.Add("  Read  ")!;
        var b = _store.Add("Run")!;

        Assert.Null(_store.Add("   "));
        Assert.Equal(["Read", "Run"], _store.Habits().Select(h => h.Value.Title));
        Assert.Equal(Today, _store.Get(a)!.StartDay);

        Assert.True(_store.Move(b, -1));
        Assert.Equal(["Run", "Read"], _store.Habits().Select(h => h.Value.Title));
        Assert.False(_store.Move(b, -1));

        Assert.True(_store.Rename(a, "Read 20 pages"));
        Assert.False(_store.Rename(a, " "));
        Assert.Equal("Read 20 pages", _store.Find("read 20 PAGES")!.Value.Title);
    }

    [Fact]
    public void Deleting_a_habit_deletes_its_days_and_reasons()
    {
        var id = NewHabit("Read", D(10, 1));
        var other = NewHabit("Run", D(10, 1));
        Done(id, D(10, 2), D(10, 3));
        Done(other, D(10, 2));
        Assert.True(_store.WriteReason(id, D(10, 4), D(10, 6), "Travel"));

        Assert.True(_store.Delete(id));

        Assert.Null(_store.Get(id));
        Assert.Single(_checks.All());
        Assert.Empty(_lapses.All());
    }

    // ---- Statistics ----------------------------------------------------------------------------------------------

    [Fact]
    public void Days_before_the_start_and_an_unfinished_today_do_not_count()
    {
        var habit = new Habit { Title = "Read", StartDay = D(10, 21) };
        // 21–29 tracked (9 days), today not done yet.
        var done = Days(D(10, 21), D(10, 22), D(10, 27), D(10, 28), D(10, 29));

        var s = HabitStats.Compute(habit, done, Today);

        Assert.False(s.DoneToday);
        Assert.Equal(9, s.Tracked30);
        Assert.Equal(5, s.Done30);
        Assert.Equal(5.0 / 9, s.Rate30!.Value, 6);
        Assert.Equal(9, s.TrackedYear);
        // The chain is not broken while today can still be done.
        Assert.Equal(3, s.CurrentStreak);
        Assert.Equal(3, s.LongestStreak);
        Assert.Equal(0, s.MissedRun);

        done.Add(Today);
        s = HabitStats.Compute(habit, done, Today);
        Assert.Equal(10, s.Tracked30);
        Assert.Equal(4, s.CurrentStreak);
    }

    [Fact]
    public void A_new_habit_has_no_rate_yet_and_a_neutral_colour()
    {
        var s = HabitStats.Compute(new Habit { Title = "Read", StartDay = Today }, Days(), Today);

        Assert.Null(s.Rate30);
        Assert.Null(s.RateYear);
        Assert.Equal(HabitColors.Neutral, HabitColors.ForRate(s.Rate30));
    }

    [Fact]
    public void Streaks_run_across_the_new_year_and_the_longest_can_be_per_year()
    {
        var done = Days(D(12, 30, 2025), D(12, 31, 2025), D(1, 1), D(1, 2), D(3, 1));

        Assert.Equal(4, HabitStats.LongestStreak(done));
        Assert.Equal(2, HabitStats.LongestStreak(done, 2026));
        Assert.Equal(2, HabitStats.LongestStreak(done, 2025));
        Assert.Equal(4, HabitStats.CurrentStreak(done, D(1, 3)));
        Assert.Equal(0, HabitStats.CurrentStreak(done, D(1, 4)));
    }

    [Fact]
    public void Missed_days_in_a_row_end_yesterday()
    {
        var habit = new Habit { Title = "Read", StartDay = D(10, 1) };

        Assert.Equal(3, HabitStats.MissedRun(habit, Days(D(10, 26)), Today));
        Assert.Equal(0, HabitStats.MissedRun(habit, Days(D(10, 29)), Today));
        // Never before the start.
        Assert.Equal(2, HabitStats.MissedRun(habit with { StartDay = D(10, 28) }, Days(), Today));
    }

    [Fact]
    public void Months_count_only_tracked_days()
    {
        var habit = new Habit { Title = "Read", StartDay = D(9, 16) };
        var done = Days(D(9, 16), D(9, 17), D(10, 1));

        var months = HabitStats.Months(habit, done, Today, 2026);

        Assert.Equal(12, months.Count);
        Assert.Null(months[7].Rate);
        Assert.Equal(new HabitMonth(9, 2, 15), months[8]);
        Assert.Equal(new HabitMonth(10, 1, 29), months[9]);
        Assert.Equal(0, months[10].Tracked);
    }

    [Fact]
    public void Cards_go_from_red_through_amber_to_green()
    {
        Assert.Equal(HabitColors.Red, HabitColors.ForRate(0));
        Assert.Equal(HabitColors.Amber, HabitColors.ForRate(0.5));
        Assert.Equal(HabitColors.Green, HabitColors.ForRate(1));
        Assert.Equal(HabitColors.Green, HabitColors.ForRate(1.5));
        var between = HabitColors.ForRate(0.75);
        Assert.NotEqual(HabitColors.Amber, between);
        Assert.NotEqual(HabitColors.Green, between);
        Assert.Equal("#1C30A46C", HabitColors.Hex(HabitColors.WithAlpha(HabitColors.Green, 0x1C)));

        Assert.Equal([0, 1, 2, 2, 3, 3, 3, 4, 4], Enumerable.Range(0, 9).Select(HabitColors.Level));
    }

    // ---- The year grid -------------------------------------------------------------------------------------------

    [Fact]
    public void The_grid_has_a_column_per_week_with_monday_on_top()
    {
        var habit = new Habit { Title = "Read", StartDay = D(10, 20) };
        var grid = HabitYearGrid.Build("h", habit, Days(D(10, 21), D(10, 22), D(10, 29)), Today, 2026);

        Assert.Equal(365, grid.Cells.Count);
        Assert.Equal(53, grid.Columns);
        // 2026-01-01 is a Thursday.
        Assert.Equal((0, 3), (grid.Cells[0].Column, grid.Cells[0].Row));
        Assert.Equal(D(1, 5), grid.At(1, 0)!.Value.Day);
        Assert.Null(grid.At(0, 0));
        Assert.Equal(["Jan", "Feb"], grid.Months.Take(2).Select(m => m.Name));
        Assert.Equal(0, grid.Months[0].Column);

        HabitGridCell Cell(DateOnly d) => grid.Cells.Single(c => c.Day == d);
        Assert.Equal(HabitCellState.BeforeStart, Cell(D(10, 19)).State);
        Assert.Equal(HabitCellState.Missed, Cell(D(10, 20)).State);
        Assert.Equal(HabitCellState.Done, Cell(D(10, 21)).State);
        Assert.Equal(1, Cell(D(10, 21)).Level);
        Assert.Equal(2, Cell(D(10, 22)).Level);
        Assert.Equal(HabitCellState.Missed, Cell(Today).State);
        Assert.True(Cell(Today).IsToday);
        Assert.Equal(HabitCellState.Future, Cell(D(10, 31)).State);
        Assert.Equal(HabitYearGrid.Column(Today), grid.FocusColumn);
    }

    [Theory]
    [InlineData(2012, 54)] // a leap year starting on a Sunday
    [InlineData(2024, 53)]
    [InlineData(2023, 53)]
    public void A_year_takes_53_or_54_weeks(int year, int columns)
    {
        var habit = new Habit { Title = "Read", StartDay = new DateOnly(year, 1, 1) };
        var grid = HabitYearGrid.Build("h", habit, Days(), new DateOnly(year, 12, 31), year);

        Assert.Equal(columns, grid.Columns);
        Assert.Equal(DateTime.IsLeapYear(year) ? 366 : 365, grid.Cells.Count);
    }

    [Fact]
    public void A_point_finds_its_cell()
    {
        var g = HabitGridGeometry.Fit(30 + 53 * 10 + 52 * 3, 53, 5, 13);

        Assert.Equal(10, g.Cell, 6);
        Assert.Equal((0, 0), g.Hit(31, 17, 53));
        Assert.Equal((2, 1), g.Hit(30 + 2 * 13 + 1, 16 + 13 + 1, 53));
        Assert.Null(g.Hit(5, 30, 53));
        Assert.Null(g.Hit(30 + 53 * 13 + 1, 20, 53));
        // Narrow: the cells stop shrinking at the minimum.
        Assert.Equal(5, HabitGridGeometry.Fit(100, 53, 5, 13).Cell);
    }

    // ---- Reasons for missed days ---------------------------------------------------------------------------------

    [Fact]
    public void Two_or_more_missed_days_in_a_row_need_a_reason()
    {
        var habit = new Habit { Title = "Read", StartDay = D(10, 1) };
        // Missed: 10/3 (one day), 10/5–10/7, 10/28–10/29; today does not count.
        var done = Days(D(10, 1), D(10, 2), D(10, 4), D(10, 8), D(10, 9), D(10, 10), D(10, 11), D(10, 12), D(10, 13), D(10, 14), D(10, 15),
            D(10, 16), D(10, 17), D(10, 18), D(10, 19), D(10, 20), D(10, 21), D(10, 22), D(10, 23), D(10, 24), D(10, 25), D(10, 26), D(10, 27));

        var gaps = HabitLapses.Pending("h", habit, done, [], Today);

        Assert.Equal([new HabitGap("h", D(10, 5), D(10, 7)), new HabitGap("h", D(10, 28), D(10, 29))], gaps);

        var reasons = new[] { new HabitLapse { HabitId = "h", From = D(10, 5), To = D(10, 7), Reason = "Flu" } };
        Assert.Equal([new HabitGap("h", D(10, 28), D(10, 29))], HabitLapses.Pending("h", habit, done, reasons, Today));
    }

    [Fact]
    public void A_run_that_keeps_growing_asks_again_after_two_more_days()
    {
        var habit = new Habit { Title = "Read", StartDay = D(10, 25) };
        var done = Days(D(10, 25));
        var reasons = new[] { new HabitLapse { HabitId = "h", From = D(10, 26), To = D(10, 27), Reason = "Busy" } };

        // 10/28 alone is not enough yet…
        Assert.Empty(HabitLapses.Pending("h", habit, done, reasons, D(10, 29)));
        // …10/28–10/29 is.
        Assert.Equal([new HabitGap("h", D(10, 28), D(10, 29))], HabitLapses.Pending("h", habit, done, reasons, Today));
    }

    [Fact]
    public void Old_runs_and_days_before_the_start_are_not_asked_about()
    {
        var habit = new Habit { Title = "Read", StartDay = D(8, 1) };
        // Nothing done since August: only the last 30 days are asked about, as one run.
        var gaps = HabitLapses.Pending("h", habit, Days(), [], Today);
        Assert.Equal([new HabitGap("h", Today.AddDays(-30), D(10, 29))], gaps);

        // Started yesterday: one missed day is not a run.
        Assert.Empty(HabitLapses.Pending("h", habit with { StartDay = D(10, 29) }, Days(), [], Today));
    }

    [Fact]
    public void The_missed_days_of_a_year_carry_their_reason()
    {
        var habit = new Habit { Title = "Read", StartDay = D(10, 20) };
        var done = Days(D(10, 20), D(10, 23), D(10, 24), D(10, 25), D(10, 26), D(10, 27));
        var lapses = new[] { new HabitLapse { HabitId = "h", From = D(10, 21), To = D(10, 22), Reason = "Sick" } };

        var missed = HabitLapses.Missed("h", habit, done, lapses, Today, 2026);

        Assert.Equal(2, missed.Count);
        Assert.Equal((D(10, 21), D(10, 22), "Sick"), (missed[0].Gap.From, missed[0].Gap.To, missed[0].Reason!.Reason));
        Assert.Equal((D(10, 28), D(10, 29)), (missed[1].Gap.From, missed[1].Gap.To));
        Assert.Null(missed[1].Reason);
        Assert.Empty(HabitLapses.Missed("h", habit, done, lapses, Today, 2025));
    }

    // ---- Reminders -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_reminder_comes_once_a_day_from_its_hour_for_habits_skipped_two_days()
    {
        var reminders = new HabitReminderService(_store, _settings);
        var slipping = NewHabit("Read", D(10, 1));
        var fine = NewHabit("Run", D(10, 1));
        Done(slipping, D(10, 27));
        Done(fine, D(10, 29));

        // 09:00, before the default 20:00.
        Assert.Null(reminders.TakeDue());

        _time.Now = T0.AddHours(11);
        var reminder = reminders.TakeDue();
        Assert.NotNull(reminder);
        Assert.Equal(1, reminder.Count);
        Assert.Contains("Read: skipped for 2 days", reminder.Message);
        Assert.Null(reminders.TakeDue());

        // Done today: nothing to say the next time either.
        _time.Now = T0.AddDays(1).AddHours(11);
        Done(slipping, D(10, 31));
        Assert.Null(HabitReminderService.Build(_store));
    }

    // ---- The view model ------------------------------------------------------------------------------------------

    private HabitsViewModel ViewModel(Helm.Shell.Services.IDialogService? dialogs = null) =>
        new(_store, _settings, new InlineUi(), dialogs ?? new AcceptDialogs(), new HabitReminderService(_store, _settings), NullLogger<HabitsViewModel>.Instance);

    [Fact]
    public async Task Tapping_a_card_asks_then_ticks_today_moves_it_down_and_celebrates()
    {
        var read = NewHabit("Read", D(10, 20));
        var run = NewHabit("Run", D(10, 20));
        Done(read, D(10, 28), D(10, 29));
        var vm = ViewModel();
        var big = new List<bool>();
        vm.Celebrated += (_, b) => big.Add(b);

        Assert.Equal(["Read", "Run"], vm.ToDo.Select(c => c.Title));
        await vm.TapCardCommand.ExecuteAsync(vm.ToDo[0]);

        Assert.True(_store.IsDone(read, Today));
        Assert.Equal(["Run"], vm.ToDo.Select(c => c.Title));
        Assert.Equal(["Read"], vm.DoneToday.Select(c => c.Title));
        Assert.True(vm.HasCelebration);
        Assert.Contains("3 days in a row", vm.Celebration!.Message);
        Assert.Equal([false], big);

        // The last habit of the day is a big one.
        await vm.TapCardCommand.ExecuteAsync(vm.ToDo[0]);
        Assert.True(vm.AllDone);
        Assert.Equal([false, true], big);
    }

    [Fact]
    public async Task Saying_no_changes_nothing_and_a_done_card_can_be_undone()
    {
        var read = NewHabit("Read", D(10, 20));
        var vm = ViewModel(new RefuseDialogs());

        await vm.TapCardCommand.ExecuteAsync(vm.ToDo[0]);
        Assert.False(_store.IsDone(read, Today));
        Assert.False(vm.HasCelebration);

        vm = ViewModel();
        await vm.TapCardCommand.ExecuteAsync(vm.ToDo[0]);
        await vm.TapCardCommand.ExecuteAsync(vm.DoneToday[0]);
        Assert.False(_store.IsDone(read, Today));
        Assert.Single(vm.ToDo);
    }

    [Fact]
    public async Task A_week_in_a_row_is_a_big_celebration()
    {
        var read = NewHabit("Read", D(10, 1));
        NewHabit("Run", D(10, 1));
        for (var d = 24; d <= 29; d++) Done(read, D(10, d));
        var vm = ViewModel();
        var big = new List<bool>();
        vm.Celebrated += (_, b) => big.Add(b);

        await vm.TapCardCommand.ExecuteAsync(vm.ToDo.First(c => c.Title == "Read"));

        Assert.Equal([true], big);
        Assert.True(vm.IsBigCelebration);
        Assert.Contains("7 days in a row", vm.Celebration!.Message);
    }

    [Fact]
    public async Task A_past_day_on_the_grid_can_be_ticked_or_unticked_but_not_one_before_the_start()
    {
        var read = NewHabit("Read", D(10, 20));
        var vm = ViewModel();

        await vm.TapDayCommand.ExecuteAsync(new HabitDayTap(read, D(10, 22)));
        Assert.True(_store.IsDone(read, D(10, 22)));
        Assert.False(vm.HasCelebration);

        await vm.TapDayCommand.ExecuteAsync(new HabitDayTap(read, D(10, 22)));
        Assert.False(_store.IsDone(read, D(10, 22)));

        await vm.TapDayCommand.ExecuteAsync(new HabitDayTap(read, D(10, 10)));
        Assert.Empty(_checks.All());
        Assert.Contains("do not count", vm.Message);

        await vm.TapDayCommand.ExecuteAsync(new HabitDayTap(read, Today));
        Assert.True(vm.HasCelebration);
    }

    [Fact]
    public async Task Skipped_days_must_be_explained_before_going_on()
    {
        var read = NewHabit("Read", D(10, 20));
        Done(read, D(10, 20), D(10, 21), D(10, 22), D(10, 23), D(10, 24), D(10, 25));
        var vm = ViewModel();

        vm.CheckLapses();
        Assert.True(vm.HasPendingLapse);
        Assert.Equal(new HabitGap(read, D(10, 26), D(10, 29)), vm.PendingLapse!.Gap);
        Assert.Contains("for 4 days", vm.PendingLapse.Heading);

        // Nothing else works meanwhile, and an empty reason does not count.
        await vm.TapCardCommand.ExecuteAsync(vm.ToDo[0]);
        Assert.False(_store.IsDone(read, Today));
        vm.LapseReason = "   ";
        Assert.False(vm.CanSaveReason);
        vm.SaveReasonCommand.Execute(null);
        Assert.True(vm.HasPendingLapse);

        vm.LapseReason = "Night shifts all week";
        vm.SaveReasonCommand.Execute(null);

        Assert.False(vm.HasPendingLapse);
        var lapse = Assert.Single(_store.Lapses(read));
        Assert.Equal((D(10, 26), D(10, 29), "Night shifts all week"), (lapse.From, lapse.To, lapse.Reason));
        vm.CheckLapses();
        Assert.False(vm.HasPendingLapse);
    }

    [Fact]
    public async Task Days_done_but_not_ticked_can_be_ticked_instead_of_explained()
    {
        var read = NewHabit("Read", D(10, 25));
        Done(read, D(10, 25));
        var vm = ViewModel();
        vm.CheckLapses();

        await vm.MarkLapseDoneCommand.ExecuteAsync(null);

        Assert.False(vm.HasPendingLapse);
        Assert.Equal(5, _store.DoneDays(read).Count);
        Assert.Empty(_store.Lapses());
    }

    [Fact]
    public void A_reason_written_on_another_device_closes_the_question()
    {
        var read = NewHabit("Read", D(10, 26));
        var vm = ViewModel();
        vm.CheckLapses();
        Assert.True(vm.HasPendingLapse);

        _lapses.SetRemote(HabitsStore.CheckId(read, D(10, 26)),
            new HabitLapse { HabitId = read, From = D(10, 26), To = D(10, 29), Reason = "Written on the phone" });

        Assert.False(vm.HasPendingLapse);
    }

    [Fact]
    public void The_report_shows_the_chosen_year_with_its_months_and_missed_days()
    {
        var read = NewHabit("Read", D(12, 20, 2025));
        Done(read, D(12, 20, 2025), D(10, 27), D(10, 28));
        _store.WriteReason(read, D(10, 25), D(10, 26), "Moving house");
        var vm = ViewModel();

        Assert.Equal("Read", vm.ReportHabit!.Title);
        Assert.Equal([2026, 2025], vm.ReportYears);
        Assert.Equal(2026, vm.ReportYear);
        Assert.True(vm.HasReport);
        Assert.Equal(2026, vm.ReportGrid!.Year);
        Assert.Equal("2 days", vm.ReportLongest);
        Assert.Equal(12, vm.ReportMonths.Count);
        Assert.True(vm.ReportMonths[9].HasDays);
        Assert.Equal("Moving house", vm.ReportLapses.First(l => l.HasReason).Reason);
        Assert.Contains(vm.ReportLapses, l => !l.HasReason);

        vm.ReportYear = 2025;
        Assert.Equal(2025, vm.ReportGrid!.Year);
        Assert.Equal("1 of 12 days", vm.ReportDone);
    }

    [Fact]
    public async Task Habits_are_added_renamed_moved_and_deleted_on_the_settings_page()
    {
        var vm = ViewModel();
        Assert.True(vm.HasNoHabits);

        vm.NewHabitTitle = "Read";
        vm.AddHabitCommand.Execute(null);
        vm.NewHabitTitle = "Run";
        vm.AddHabitCommand.Execute(null);

        Assert.Equal("", vm.NewHabitTitle);
        Assert.Equal(["Read", "Run"], vm.ManageHabits.Select(r => r.Title));
        Assert.False(vm.ManageHabits[0].CanMoveUp);

        vm.ManageHabits[0].Title = "Read 20 pages";
        Assert.Equal("Read 20 pages", _store.Habits()[0].Value.Title);
        Assert.Equal("Read 20 pages", vm.ToDo[0].Title);

        vm.MoveHabitDownCommand.Execute(vm.ManageHabits[0]);
        Assert.Equal(["Run", "Read 20 pages"], vm.ManageHabits.Select(r => r.Title));

        await vm.DeleteHabitCommand.ExecuteAsync(vm.ManageHabits[0]);
        Assert.Equal(["Read 20 pages"], _store.Habits().Select(h => h.Value.Title));
        Assert.Single(vm.ToDo);
    }

    // ---- MCP -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Agents_read_the_habits_and_the_reasons_and_can_tick_a_day()
    {
        var read = NewHabit("Read", D(10, 20));
        Done(read, D(10, 20), D(10, 21), D(10, 24));
        _store.WriteReason(read, D(10, 22), D(10, 23), "Exams");
        var tools = new HabitsMcpTools(_store).Tools.ToList();
        Assert.True(tools.Single(t => t.Name == "habits_list").ReadOnly);
        Assert.True(tools.Single(t => t.Name == "habit_report").ReadOnly);
        Assert.Equal(McpRisk.Change, tools.Single(t => t.Name == "habit_check").Risk);
        var server = new McpServer(tools, "0.67.0", McpEndpoint.Instructions);

        var (error, text) = await Call(server, "habits_list", []);
        Assert.False(error);
        var list = JsonNode.Parse(text)!["habits"]![0]!;
        Assert.Equal("Read", list["title"]!.GetValue<string>());
        Assert.Equal(5, list["skippedDaysInARow"]!.GetValue<int>());

        (error, text) = await Call(server, "habit_report", new JsonObject { ["habit"] = "read" });
        Assert.False(error);
        var report = JsonNode.Parse(text)!;
        Assert.Equal(3, report["daysDone"]!.GetValue<int>());
        Assert.Equal("Exams", report["missed"]![0]!["reason"]!.GetValue<string>());
        Assert.Null(report["missed"]![1]!["reason"]);

        (error, text) = await Call(server, "habit_check", new JsonObject { ["habit"] = read, ["day"] = "2026-10-29" });
        Assert.False(error);
        Assert.True(_store.IsDone(read, D(10, 29)));

        (error, text) = await Call(server, "habit_check", new JsonObject { ["habit"] = read, ["day"] = "2026-10-01" });
        Assert.True(error);
        Assert.Contains("does not count", text);
    }

    private static async Task<(bool IsError, string Text)> Call(McpServer server, string tool, JsonObject args)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
        };
        var reply = await server.HandleAsync(request.ToJsonString(), default);
        var result = reply!["result"]!;
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }

    private sealed class RefuseDialogs : Helm.Shell.Services.IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);
    }
}
