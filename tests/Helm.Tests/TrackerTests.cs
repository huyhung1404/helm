using System.Globalization;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Tracker;
using Helm.Modules.Wallet;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public sealed class TrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(T0);
    private readonly MemoryCollection<TrackerWorkspace> _workspaces = new();
    private readonly MemoryCollection<TrackerItem> _items = new();
    private readonly MemoryLog<TrackerEvent> _history = new();
    private readonly TrackerStore _store;

    public TrackerTests()
    {
        _store = new TrackerStore(_workspaces, _items, _history, _time);
    }

    [Fact]
    public void Completing_records_created_started_and_completed_times_in_the_history()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var id = _store.AddItem(ws, new TrackerItemDraft("Write report"));
        _time.Advance(TimeSpan.FromMinutes(90));

        Assert.True(_store.Complete(id));
        Assert.False(_store.Complete(id)); // already done

        var item = _store.GetItem(id)!;
        Assert.Equal(T0.AddMinutes(90), item.CompletedAt);
        Assert.Equal(T0, item.StartedAt); // never started: counts from creation
        Assert.False(item.StartedExplicitly);

        var history = _store.History();
        Assert.Equal([TrackerEventKind.Created, TrackerEventKind.Completed], history.Select(e => e.Kind));
        var done = history[1];
        Assert.Equal(id, done.ItemId);
        Assert.Equal("Write report", done.Title);
        Assert.Equal(T0, done.CreatedAt);
        Assert.Equal(T0.AddMinutes(90), done.CompletedAt);
        Assert.Equal(T0.AddMinutes(90), done.At);
    }

    [Fact]
    public void Start_is_kept_and_reported_as_working_time()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var id = _store.AddItem(ws, new TrackerItemDraft("Fix bug"));
        _time.Advance(TimeSpan.FromHours(2));
        Assert.True(_store.Start(id));
        Assert.False(_store.Start(id)); // already started
        _time.Advance(TimeSpan.FromMinutes(30));
        _store.Complete(id);

        var item = _store.GetItem(id)!;
        Assert.True(item.StartedExplicitly);
        Assert.Equal(T0.AddHours(2), item.StartedAt);

        var report = TrackerReport.Build(_store.History(), ReportRange.Last7Days, _time.Now, TimeZoneInfo.Utc);
        Assert.Equal(1, report.CompletedCount);
        Assert.Equal(TimeSpan.FromMinutes(150), report.AverageLeadTime);
        Assert.Equal(TimeSpan.FromMinutes(30), report.AverageWorkTime);
    }

    [Fact]
    public void Reopening_drops_an_implicit_start_and_reports_count_only_the_last_completion()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var id = _store.AddItem(ws, new TrackerItemDraft("Call bank"));
        _time.Advance(TimeSpan.FromHours(1));
        _store.Complete(id);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(_store.Reopen(id));

        var reopened = _store.GetItem(id)!;
        Assert.Null(reopened.CompletedAt);
        Assert.Null(reopened.StartedAt);
        Assert.Equal(0, TrackerReport.Build(_store.History(), ReportRange.AllTime, _time.Now, TimeZoneInfo.Utc).CompletedCount);

        _time.Advance(TimeSpan.FromHours(1));
        _store.Complete(id);
        var report = TrackerReport.Build(_store.History(), ReportRange.AllTime, _time.Now, TimeZoneInfo.Utc);
        Assert.Equal(1, report.CompletedCount);
        Assert.Equal(TimeSpan.FromHours(3), report.AverageLeadTime);
    }

    [Fact]
    public void Open_items_sort_by_priority_then_manual_order_and_move_stays_within_a_priority()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var a = _store.AddItem(ws, new TrackerItemDraft("a"));
        var b = _store.AddItem(ws, new TrackerItemDraft("b"));
        var urgent = _store.AddItem(ws, new TrackerItemDraft("urgent", TrackerPriority.Urgent));
        var low = _store.AddItem(ws, new TrackerItemDraft("low", TrackerPriority.Low));

        Assert.Equal([urgent, a, b, low], _store.OpenItems(ws).Select(i => i.Id));

        Assert.True(_store.Move(b, -1));
        Assert.Equal([urgent, b, a, low], _store.OpenItems(ws).Select(i => i.Id));
        Assert.False(_store.Move(b, -1)); // would cross into Urgent
        Assert.False(_store.Move(low, 1)); // already last

        // Changing the priority moves the item to the end of its new group.
        _store.UpdateItem(a, i => i with { Priority = TrackerPriority.Urgent });
        Assert.Equal([urgent, a, b, low], _store.OpenItems(ws).Select(i => i.Id));
    }

    [Fact]
    public void Moving_works_even_when_two_devices_gave_items_the_same_order()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var a = _store.AddItem(ws, new TrackerItemDraft("a"));
        var b = _store.AddItem(ws, new TrackerItemDraft("b"));
        _items.Upsert(b, _items.Get(b)! with { Order = _items.Get(a)!.Order });

        var first = _store.OpenItems(ws)[0].Id;
        var second = _store.OpenItems(ws)[1].Id;
        Assert.True(_store.Move(second, -1));
        Assert.Equal([second, first], _store.OpenItems(ws).Select(i => i.Id));
    }

    [Fact]
    public void Deleting_a_workspace_removes_its_items_but_keeps_the_history()
    {
        var ws = _store.AddWorkspace("Old", WorkspaceKind.Tasks);
        var other = _store.AddWorkspace("Other", WorkspaceKind.Tasks);
        var id = _store.AddItem(ws, new TrackerItemDraft("x"));
        _store.Complete(id);
        _store.AddItem(other, new TrackerItemDraft("y"));

        Assert.True(_store.DeleteWorkspace(ws));

        Assert.Null(_store.GetItem(id));
        Assert.Equal(["Other"], _store.Workspaces().Select(w => w.Value.Name));
        Assert.Single(_store.AllItems());
        Assert.Equal(1, TrackerReport.Build(_store.History(), ReportRange.AllTime, _time.Now, TimeZoneInfo.Utc, ws).CompletedCount);
    }

    [Fact]
    public void The_old_debt_book_is_hidden_refused_and_handed_over_to_wallet_once()
    {
        // A debt book as Helm 0.26 wrote it: a workspace of kind Debts and its entries.
        var book = _workspaces.Add(new TrackerWorkspace { Name = "Sổ nợ", Kind = WorkspaceKind.Debts, Currency = "₫", CreatedAt = T0 });
        var lunch = _items.Add(new TrackerItem { WorkspaceId = book, Person = "Minh Anh", Amount = 500_000, Direction = DebtDirection.TheyOweMe, Title = "lunch", CreatedAt = T0 });
        var paid = _items.Add(new TrackerItem
        {
            WorkspaceId = book, Person = "Lan", Amount = 50_000, Direction = DebtDirection.IOwe, CreatedAt = T0,
            DueDate = new DateOnly(2026, 10, 1), CompletedAt = T0.AddDays(1),
        });
        _history.Append(TrackerEvent.For(TrackerEventKind.Created, T0, lunch, _items.Get(lunch)!, WorkspaceKind.Debts));
        var tasks = _store.AddWorkspace("To-do");
        _store.Complete(_store.AddItem(tasks, new TrackerItemDraft("Ship it")));

        // The Tracker shows only to-do lists now, and makes no new debt book.
        Assert.Equal([tasks], _store.Workspaces().Select(w => w.Id));
        Assert.Single(_store.AllItems());
        Assert.Throws<InvalidOperationException>(() => _store.AddWorkspace("Debts", WorkspaceKind.Debts));
        Assert.Throws<InvalidOperationException>(() => _store.AddItem(book, new TrackerItemDraft("x")));
        Assert.Equal(1, TrackerReport.Build(_store.History(), ReportRange.AllTime, _time.Now, TimeZoneInfo.Utc).CreatedCount);
        Assert.DoesNotContain("lunch", TrackerCsv.History(_store.History(), _store.Workspaces().ToDictionary(w => w.Id, w => w.Value)));

        // Handed over with the same ids, once: a deletion in Wallet stays deleted.
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), wallet, _time);
        var handover = new TrackerDebtHandover(_store, debts);
        Assert.Equal(2, handover.Run());
        var moved = debts.Get(lunch)!;
        Assert.Equal(("Minh Anh", 500_000m, DebtDirection.TheyOweMe, "lunch"), (moved.Person, moved.Amount, moved.Direction, moved.Note));
        Assert.Equal(T0.AddDays(1), debts.Get(paid)!.SettledAt);
        Assert.Equal(new DateOnly(2026, 10, 1), debts.Get(paid)!.DueDate);
        Assert.Equal(500_000m, debts.Balance("minh anh"));
        Assert.All(_items.All().Where(i => i.Value.WorkspaceId == book), i => Assert.True(i.Value.MovedToWallet));
        Assert.NotNull(_workspaces.Get(book)); // kept for older versions

        debts.Delete(lunch);
        Assert.Equal(0, handover.Run());
        Assert.Null(debts.Get(lunch));

        // An older Helm adds an entry meanwhile: the next run copies just that one.
        var later = _items.Add(new TrackerItem { WorkspaceId = book, Person = "Lan", Amount = 20_000, Direction = DebtDirection.TheyOweMe, CreatedAt = T0.AddDays(2) });
        Assert.Equal(1, handover.Run());
        Assert.Equal(20_000m, debts.Get(later)!.Amount);
    }

    [Fact]
    public void A_task_needs_a_title()
    {
        var tasks = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        Assert.Throws<ArgumentException>(() => _store.AddItem(tasks, new TrackerItemDraft("  ")));
        Assert.Throws<ArgumentException>(() => _store.AddWorkspace(" "));
    }

    [Theory]
    [InlineData(0.4, "<1m")]
    [InlineData(45, "45m")]
    [InlineData(120, "2h")]
    [InlineData(200, "3h 20m")]
    [InlineData(1440 * 2 + 240, "2d 4h")]
    public void Durations_are_short(double minutes, string expected) =>
        Assert.Equal(expected, TrackerFormat.Duration(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Report_buckets_completions_per_day_within_the_range()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var old = _store.AddItem(ws, new TrackerItemDraft("old"));
        _store.Complete(old); // 28 Sep
        _time.Advance(TimeSpan.FromDays(10)); // 8 Oct
        var late = _store.AddItem(ws, new TrackerItemDraft("late", DueDate: new DateOnly(2026, 10, 7)));
        var onTime = _store.AddItem(ws, new TrackerItemDraft("on time", TrackerPriority.High, new DateOnly(2026, 10, 9)));
        _store.Complete(late);
        _store.Complete(onTime);

        var week = TrackerReport.Build(_store.History(), ReportRange.Last7Days, _time.Now, TimeZoneInfo.Utc);
        Assert.Equal(7, week.Days.Count);
        Assert.Equal(new DateOnly(2026, 10, 2), week.Days[0].Date);
        Assert.Equal(new ReportDay(new DateOnly(2026, 10, 8), 2), week.Days[^1]);
        Assert.Equal(2, week.CompletedCount);
        Assert.Equal(2, week.CreatedCount);
        Assert.Equal(0.5, week.OnTimeRate);
        Assert.Equal(1, week.ByPriority[TrackerPriority.High]);

        var all = TrackerReport.Build(_store.History(), ReportRange.AllTime, _time.Now, TimeZoneInfo.Utc);
        Assert.Equal(3, all.CompletedCount);
        Assert.Equal(11, all.Days.Count); // from the first completion to today
    }

    [Fact]
    public void Csv_quotes_text_and_defuses_formulas()
    {
        Assert.Equal("plain", TrackerCsv.Escape("plain"));
        Assert.Equal("\"a, b\"", TrackerCsv.Escape("a, b"));
        Assert.Equal("\"say \"\"hi\"\"\"", TrackerCsv.Escape("say \"hi\""));
        Assert.Equal("'=SUM(A1)", TrackerCsv.Escape("=SUM(A1)"));

        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var id = _store.AddItem(ws, new TrackerItemDraft("Dinner, drinks", Notes: "=1+1"));
        var csv = TrackerCsv.Items(_store.AllItems(), _store.Workspaces().ToDictionary(w => w.Id, w => w.Value));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("workspace,kind,title,priority,status,", lines[0]);
        Assert.EndsWith(",lead_minutes,work_minutes,notes,id", lines[0]);
        Assert.Contains("\"Dinner, drinks\"", lines[1]);
        Assert.Contains(",'=1+1,", lines[1]);
        Assert.EndsWith(id, lines[1]);
    }

    [Fact]
    public void Widget_shows_the_chosen_workspace_and_cycles_through_them()
    {
        var off = TrackerWidgetModel.Build(_store, null, enabled: false, _time.Now, TimeZoneInfo.Utc);
        Assert.Empty(off.Rows);
        Assert.Contains("turned off", off.EmptyText);
        Assert.Null(TrackerWidgetModel.Build(_store, null, true, _time.Now, TimeZoneInfo.Utc).WorkspaceId);

        var tasks = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var home = _store.AddWorkspace("Home", WorkspaceKind.Tasks);
        _store.AddItem(tasks, new TrackerItemDraft("later", TrackerPriority.Low));
        _store.AddItem(tasks, new TrackerItemDraft("now", TrackerPriority.Urgent, new DateOnly(2026, 9, 28)));
        _store.AddItem(home, new TrackerItemDraft("water plants"));

        var first = TrackerWidgetModel.Build(_store, "deleted-workspace", true, _time.Now, TimeZoneInfo.Utc);
        Assert.Equal(tasks, first.WorkspaceId);
        Assert.Equal(["now", "later"], first.Rows.Select(r => r.Title));
        Assert.Equal("Urgent · Due today", first.Rows[0].Details);

        Assert.Equal(home, TrackerWidgetModel.NextWorkspace(_store, tasks));
        Assert.Equal(tasks, TrackerWidgetModel.NextWorkspace(_store, home));
        Assert.Equal("water plants", Assert.Single(TrackerWidgetModel.Build(_store, home, true, _time.Now, TimeZoneInfo.Utc).Rows).Title);
    }

    [Fact]
    public void View_model_adds_completes_and_reports_bad_input()
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var settings = new SettingsStoreFactory(new HelmPaths(dir));
            var vm = new TrackerViewModel(_store, settings, new InlineDispatcher(),
                new YesDialogs(), new NullClipboard(), new TrackerReminderService(_store, settings, _time), NullLogger<TrackerViewModel>.Instance);
            Assert.True(vm.HasNoWorkspaces);

            vm.CreateTasksWorkspaceCommand.Execute(null);
            Assert.True(vm.IsTaskWorkspace);
            Assert.False(vm.AddCommand.CanExecute(null)); // no title yet
            vm.NewTitle = "Pay rent";
            vm.NewDueTimeText = "soon";
            vm.AddCommand.Execute(null);
            Assert.True(vm.HasMessage);
            Assert.Empty(vm.OpenItems);

            vm.NewDueTimeText = "";
            vm.AddCommand.Execute(null);
            var row = Assert.Single(vm.OpenItems);
            Assert.Equal("Pay rent", row.Title);
            Assert.Equal("", vm.NewTitle);
            row.ToggleCompleteCommand.Execute(null);
            Assert.Empty(vm.OpenItems);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { } // a settings write may still be finishing; it is only a temp folder
        }
    }

    [Fact]
    public void Subtasks_belong_to_their_task_are_finished_with_it_and_deleted_with_it()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var task = _store.AddItem(ws, new TrackerItemDraft("Release"));
        var a = _store.AddItem(ws, new TrackerItemDraft("Build", ParentId: task));
        var b = _store.AddItem(ws, new TrackerItemDraft("Test", ParentId: task));
        Assert.Equal(task, Assert.Single(_store.OpenItems(ws)).Id); // subtasks are not top-level items
        Assert.Equal([a, b], _store.Subtasks(task).Select(x => x.Id));
        Assert.Throws<InvalidOperationException>(() => _store.AddItem(ws, new TrackerItemDraft("Nested", ParentId: a)));

        _store.Complete(a);
        Assert.Equal([b, a], _store.Subtasks(task).Select(x => x.Id)); // open first
        _store.Complete(task);
        Assert.All(_store.Subtasks(task), x => Assert.True(x.Value.IsCompleted));
        _store.DeleteItem(task);
        Assert.Empty(_store.Items(ws));
    }

    [Fact]
    public void A_due_time_is_kept_with_its_day_for_older_versions_and_shown_with_the_time()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var at = TrackerDue.FromLocal(new DateTime(2026, 9, 28), new TimeSpan(14, 30, 0), TimeZoneInfo.Local)!.Value;
        var id = _store.AddItem(ws, new TrackerItemDraft("Call", DueAt: at));
        var item = _store.GetItem(id)!;
        Assert.Equal(at, item.DueAt);
        Assert.Equal(new DateOnly(2026, 9, 28), item.DueDate);
        var morning = new DateTimeOffset(new DateTime(2026, 9, 28, 9, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 28, 9, 0, 0)));
        Assert.StartsWith("Due today", TrackerFormat.Due(item, morning));
        Assert.Contains(TrackerFormat.Time(new TimeSpan(14, 30, 0)), TrackerFormat.Due(item, morning));
        Assert.StartsWith("Overdue since", TrackerFormat.Due(item, morning.AddHours(6)));
        // A day without a time is due at the end of that day.
        var dayOnly = new TrackerItem { DueDate = new DateOnly(2026, 9, 28) };
        Assert.Equal(23, TimeZoneInfo.ConvertTime(dayOnly.DueMoment(TimeZoneInfo.Local)!.Value, TimeZoneInfo.Local).Hour);
    }

    [Theory]
    [InlineData("14:30", 14, 30)]
    [InlineData("1430", 14, 30)]
    [InlineData("9", 9, 0)]
    [InlineData("9h30", 9, 30)]
    [InlineData("9h", 9, 0)]
    public void Times_are_read_as_people_type_them(string text, int hours, int minutes)
    {
        Assert.True(TrackerFormat.TryParseTime(text, out var time));
        Assert.Equal(new TimeSpan(hours, minutes, 0), time);
        Assert.True(TrackerFormat.TryParseTime("", out var none) && none is null);
        Assert.False(TrackerFormat.TryParseTime("25:00", out _));
        Assert.False(TrackerFormat.TryParseTime("soon", out _));
    }

    [Fact]
    public void The_widget_counts_open_items_and_shrinks_when_empty()
    {
        var tasks = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var model = TrackerWidgetModel.Build(_store, tasks, enabled: true, T0, TimeZoneInfo.Utc);
        Assert.True(model.IsEmpty);
        Assert.Equal(0, model.OpenCount);

        var a = _store.AddItem(tasks, new TrackerItemDraft("A"));
        _store.AddItem(tasks, new TrackerItemDraft("A1", ParentId: a)); // subtasks are not counted
        _store.AddItem(tasks, new TrackerItemDraft("B"));
        model = TrackerWidgetModel.Build(_store, tasks, enabled: true, T0, TimeZoneInfo.Utc);
        Assert.Equal(2, model.OpenCount);
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void Widget_colours_follow_the_settings()
    {
        var s = new TrackerSettings();
        Assert.Null(TrackerWidgetStyle.Background(s)); // the theme's card colour
        Assert.Null(TrackerWidgetStyle.Text(s));
        Assert.Equal(255, TrackerWidgetStyle.Alpha(s));

        s.WidgetBackground = WidgetBackground.Dark;
        s.WidgetOpacity = 50;
        Assert.Equal(128, TrackerWidgetStyle.Alpha(s));
        Assert.Equal(0xFFFFFFFFu, TrackerWidgetStyle.Text(s)!.Value.Main); // light text on dark, automatically
        s.WidgetText = WidgetText.Dark;
        Assert.Equal(0xFF1A1A1Au, TrackerWidgetStyle.Text(s)!.Value.Main);

        // Transparent: the theme's card colour at the chosen opacity, with the theme's text, never a clear widget.
        s.WidgetBackground = WidgetBackground.Transparent;
        s.WidgetText = WidgetText.Automatic;
        s.WidgetOpacity = 35;
        Assert.Null(TrackerWidgetStyle.Background(s));
        Assert.Null(TrackerWidgetStyle.Text(s));
        Assert.Equal(89, TrackerWidgetStyle.Alpha(s));
    }

    [Fact]
    public void Picking_transparent_starts_see_through()
    {
        Assert.Equal(WidgetLook.TransparentOpacity, WidgetLook.OpacityAfter(WidgetBackground.Card, WidgetBackground.Transparent, 100));
        Assert.Equal(40, WidgetLook.OpacityAfter(WidgetBackground.Dark, WidgetBackground.Transparent, 40)); // already see-through
        Assert.Equal(100, WidgetLook.OpacityAfter(WidgetBackground.Transparent, WidgetBackground.Transparent, 100));
        Assert.Equal(100, WidgetLook.OpacityAfter(WidgetBackground.Card, WidgetBackground.Dark, 100));
    }

    [Fact]
    public void Old_transparent_widget_settings_stay_see_through()
    {
        using var dir = new TempDir();
        var paths = new HelmPaths(dir.Path);
        var path = paths.SettingsFile(TrackerIds.ModuleId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"version":1,"widgetBackground":"transparent","widgetOpacity":100}""");
        using var factory = new SettingsStoreFactory(paths);
        var s = factory.Get<TrackerSettings>(TrackerIds.ModuleId).Current;
        Assert.Equal(WidgetBackground.Transparent, s.WidgetBackground);
        Assert.Equal(WidgetLook.TransparentOpacity, s.WidgetOpacity);
        Assert.Equal(TrackerSettings.CurrentVersion, s.Version);
    }

    [Fact]
    public void A_repeating_task_comes_back_each_new_day_once_with_its_time_and_subtasks()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(T0, TimeZoneInfo.Local).DateTime);
        var at = TrackerDue.FromLocal(today.ToDateTime(TimeOnly.MinValue), new TimeSpan(7, 30, 0), TimeZoneInfo.Local);
        var first = _store.AddItem(ws, new TrackerItemDraft("Exercise", DueAt: at, RepeatDaily: true));
        var series = _store.GetItem(first)!.SeriesId!;
        Assert.Equal(TrackerStore.OccurrenceId(series, today), first);
        _store.AddItem(ws, new TrackerItemDraft("Stretch", ParentId: first));
        _store.Complete(first);

        Assert.Equal(0, _store.EnsureRepeats(today)); // today's is there already
        var tomorrow = today.AddDays(1);
        Assert.Equal(1, _store.EnsureRepeats(tomorrow));
        Assert.Equal(0, _store.EnsureRepeats(tomorrow)); // once: the same id on every device
        var next = _store.GetItem(TrackerStore.OccurrenceId(series, tomorrow))!;
        Assert.Equal("Exercise", next.Title);
        Assert.False(next.IsCompleted);
        Assert.Equal(new TimeSpan(7, 30, 0), TimeZoneInfo.ConvertTime(next.DueAt!.Value, TimeZoneInfo.Local).TimeOfDay);
        Assert.Equal(tomorrow, next.DueDate);
        var sub = Assert.Single(_store.Subtasks(TrackerStore.OccurrenceId(series, tomorrow)));
        Assert.Equal("Stretch", sub.Value.Title);
        Assert.False(sub.Value.IsCompleted);
        Assert.Equal(1, _store.SeriesCompletions(series));

        // Days the app was not opened are skipped: only the day it is.
        Assert.Equal(1, _store.EnsureRepeats(today.AddDays(5)));
        Assert.Null(_store.GetItem(TrackerStore.OccurrenceId(series, today.AddDays(3))));

        // Deleting a day stops the series.
        _store.DeleteItem(TrackerStore.OccurrenceId(series, today.AddDays(5)));
        Assert.Equal(0, _store.EnsureRepeats(today.AddDays(6)));
    }

    [Fact]
    public void A_task_repeating_for_some_days_stops_after_its_last_day()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(T0, TimeZoneInfo.Local).DateTime);
        var first = _store.AddItem(ws, new TrackerItemDraft("Pills", RepeatDaily: true, RepeatUntil: today.AddDays(1)));
        Assert.Equal(1, _store.EnsureRepeats(today.AddDays(1)));
        Assert.Equal(0, _store.EnsureRepeats(today.AddDays(2)));
        Assert.Equal(2, _store.Items(ws).Count);
        Assert.NotNull(first);
    }

    [Fact]
    public void The_report_counts_the_days_a_repeating_task_was_done()
    {
        var ws = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(T0, TimeZoneInfo.Local).DateTime);
        var first = _store.AddItem(ws, new TrackerItemDraft("Read", RepeatDaily: true));
        var series = _store.GetItem(first)!.SeriesId!;
        _store.Complete(first);
        _time.Advance(TimeSpan.FromDays(1));
        _store.EnsureRepeats(today.AddDays(1));
        _store.Complete(TrackerStore.OccurrenceId(series, today.AddDays(1)));
        _store.AddItem(ws, new TrackerItemDraft("One-off"));

        var report = TrackerReport.Build(_store.History(), ReportRange.Last7Days, _time.GetUtcNow(), TimeZoneInfo.Local);
        var repeat = Assert.Single(report.Repeats);
        Assert.Equal("Read", repeat.Title);
        Assert.Equal(2, repeat.Count);
    }

    [Fact]
    public void The_view_model_adds_a_task_repeating_for_a_number_of_days()
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var settings = new SettingsStoreFactory(new HelmPaths(dir));
            var vm = new TrackerViewModel(_store, settings, new InlineDispatcher(),
                new YesDialogs(), new NullClipboard(), new TrackerReminderService(_store, settings, _time), NullLogger<TrackerViewModel>.Instance);
            vm.CreateTasksWorkspaceCommand.Execute(null);
            vm.NewTitle = "Water plants";
            vm.NewRepeatIndex = 2;
            Assert.True(vm.NewRepeatsForDays);
            vm.NewRepeatDays = "soon";
            vm.AddCommand.Execute(null);
            Assert.True(vm.HasMessage);
            Assert.Empty(vm.OpenItems);

            vm.NewRepeatDays = "3";
            vm.AddCommand.Execute(null);
            var row = Assert.Single(vm.OpenItems);
            Assert.True(row.IsRepeating);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(T0, TimeZoneInfo.Local).DateTime);
            Assert.Equal(today.AddDays(2), row.Item.RepeatUntil);
            Assert.Contains("Every day until", row.Details);
            Assert.Contains("done 0 times", row.Details);

            row.Done = true;
            Assert.Contains("done 1 time", Assert.Single(vm.CompletedItems).Details);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Ticking_the_box_completes_without_a_click_and_the_row_says_took_and_worked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var settings = new SettingsStoreFactory(new HelmPaths(dir));
            var vm = new TrackerViewModel(_store, settings, new InlineDispatcher(),
                new YesDialogs(), new NullClipboard(), new TrackerReminderService(_store, settings, _time), NullLogger<TrackerViewModel>.Instance);
            vm.CreateTasksWorkspaceCommand.Execute(null);
            vm.NewTitle = "Report";
            vm.AddCommand.Execute(null);
            var row = Assert.Single(vm.OpenItems);

            _time.Advance(TimeSpan.FromMinutes(30));
            row.StartCommand.Execute(null);
            _time.Advance(TimeSpan.FromMinutes(15));
            row.Done = true; // what UI Automation (screen readers) does: set IsChecked, no click

            Assert.Same(row, Assert.Single(vm.CompletedItems));
            Assert.True(row.Done);
            Assert.Contains("took 45m", row.Details); // from when it was added, like the history
            Assert.Contains("worked 15m", row.Details);

            row.Done = false;
            Assert.Same(row, Assert.Single(vm.OpenItems));
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { } // a settings write may still be finishing; it is only a temp folder
        }
    }

    [Fact]
    public void Reminder_lists_overdue_today_and_soon_items_but_not_done_or_undated_ones()
    {
        var tasks = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var today = new DateOnly(2026, 9, 28);
        _store.AddItem(tasks, new TrackerItemDraft("late", DueDate: today.AddDays(-2)));
        _store.AddItem(tasks, new TrackerItemDraft("now", TrackerPriority.Urgent, today));
        _store.AddItem(tasks, new TrackerItemDraft("tomorrow", DueDate: today.AddDays(1)));
        _store.AddItem(tasks, new TrackerItemDraft("next week", DueDate: today.AddDays(7)));
        _store.AddItem(tasks, new TrackerItemDraft("no date"));
        _store.Complete(_store.AddItem(tasks, new TrackerItemDraft("done", DueDate: today)));
        // Debts due are in Wallet's debt book now; the reminder still lists them.
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        var debts = new DebtBook(new MemorySynced<WalletDebt>(), wallet, _time);
        debts.Add("An", 50_000, DebtEntryKind.OwesMe, dueAt: DebtDue.FromLocal(today.ToDateTime(TimeOnly.MinValue), null, TimeZoneInfo.Local));
        debts.Add("Binh", 10_000, DebtEntryKind.IOwe);

        var reminder = TrackerReminderService.Build(_store, today, daysBefore: 1, debts)!;
        Assert.Equal((1, 2, 1), (reminder.Overdue, reminder.DueToday, reminder.DueSoon));
        Assert.Equal("Tracker: 1 overdue · 2 due today · 1 due soon", reminder.Title);
        var lines = reminder.Message.Split('\n');
        Assert.Equal("late (overdue by 2 days)", lines[0]);
        Assert.Equal("now (due today)", lines[1]); // urgent before the debt due the same day
        Assert.StartsWith("An pays back 50", lines[2]);
        Assert.EndsWith("(due today)", lines[2]);
        Assert.StartsWith("tomorrow (", lines[3]);

        Assert.Equal(3, TrackerReminderService.Build(_store, today, daysBefore: 0, debts)!.Total);
        Assert.Equal(2, TrackerReminderService.Build(_store, today, daysBefore: 0)!.Total);
        Assert.Null(TrackerReminderService.Build(new TrackerStore(new MemoryCollection<TrackerWorkspace>(), new MemoryCollection<TrackerItem>(),
            new MemoryLog<TrackerEvent>(), _time), today, 3));
    }

    [Fact]
    public void Reminder_comes_once_a_day_from_the_reminder_hour_and_waits_while_nothing_is_due()
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var settings = new SettingsStoreFactory(new HelmPaths(dir));
            var service = new TrackerReminderService(_store, settings, _time);
            var utc = TimeZoneInfo.Utc;
            _time.Now = new DateTimeOffset(2026, 9, 28, 7, 0, 0, TimeSpan.Zero); // before 9:00
            var tasks = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);

            Assert.Null(service.TakeDue(utc)); // nothing due yet
            _time.Now = _time.Now.AddHours(3); // 10:00
            Assert.Null(service.TakeDue(utc)); // still nothing due: not marked as shown
            _store.AddItem(tasks, new TrackerItemDraft("pay rent", DueDate: new DateOnly(2026, 9, 28)));

            var first = service.TakeDue(utc);
            Assert.NotNull(first);
            Assert.Null(service.TakeDue(utc)); // once a day
            Assert.Equal(new DateOnly(2026, 9, 28), settings.Get<TrackerSettings>(TrackerIds.ModuleId).Current.LastReminderDate);

            _time.Now = new DateTimeOffset(2026, 9, 29, 8, 59, 0, TimeSpan.Zero);
            Assert.Null(service.TakeDue(utc)); // next day, before the hour
            Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero), service.NextCheck(utc));
            _time.Now = _time.Now.AddMinutes(2);
            Assert.Equal(1, service.TakeDue(utc)!.Overdue);

            settings.Get<TrackerSettings>(TrackerIds.ModuleId).Update(s => { s.RemindersEnabled = false; s.LastReminderDate = null; });
            Assert.Null(service.TakeDue(utc)); // off

            TrackerReminder? requested = null;
            service.Requested += (_, r) => requested = r;
            Assert.True(service.RemindNow(utc)); // "Remind me now" ignores the hour and the switch
            Assert.NotNull(requested);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { } // a settings write may still be finishing; it is only a temp folder
        }
    }

    private sealed class MemoryCollection<T> : ISyncedCollection<T> where T : class
    {
        private readonly SortedDictionary<string, T> _records = new(StringComparer.Ordinal);
        private int _next;

        public string Name => typeof(T).Name;

        public event EventHandler<SyncedChangedEventArgs>? Changed;

        public T? Get(string id) => _records.GetValueOrDefault(id);

        public IReadOnlyList<SyncedItem<T>> All() =>
            _records.Select(r => new SyncedItem<T>(r.Key, r.Value, DateTimeOffset.UnixEpoch)).ToList();

        public void Upsert(string id, T value)
        {
            _records[id] = value;
            Changed?.Invoke(this, new SyncedChangedEventArgs([id], SyncChangeOrigin.Local));
        }

        public string Add(T value)
        {
            var id = (++_next).ToString("D6", CultureInfo.InvariantCulture);
            Upsert(id, value);
            return id;
        }

        public bool Delete(string id)
        {
            if (!_records.Remove(id)) return false;
            Changed?.Invoke(this, new SyncedChangedEventArgs([id], SyncChangeOrigin.Local));
            return true;
        }
    }

    private sealed class MemoryLog<T> : ISyncedLog<T> where T : class
    {
        private readonly MemoryCollection<T> _inner = new();

        public string Name => _inner.Name;

        public event EventHandler<SyncedChangedEventArgs>? Changed
        {
            add => _inner.Changed += value;
            remove => _inner.Changed -= value;
        }

        public string Append(T entry) => _inner.Add(entry);

        public IReadOnlyList<SyncedItem<T>> All() => _inner.All();

        public bool Remove(string id) => _inner.Delete(id);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class YesDialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);
    }

    private sealed class NullClipboard : IClipboardService
    {
        public void SetText(string text) { }
    }
}
