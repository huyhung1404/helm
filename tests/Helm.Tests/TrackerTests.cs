using System.Globalization;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Tracker;
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
    public void There_is_one_debt_book_in_vnd_but_any_number_of_to_do_lists()
    {
        var book = _store.AddWorkspace("Sổ nợ", WorkspaceKind.Debts);
        Assert.Equal("₫", _store.GetWorkspace(book)!.Currency);
        Assert.True(_store.HasDebtBook);

        var refused = Assert.Throws<InvalidOperationException>(() => _store.AddWorkspace("Nợ bạn bè", WorkspaceKind.Debts));
        Assert.Contains("Sổ nợ", refused.Message);

        _store.AddWorkspace("Work", WorkspaceKind.Tasks);
        _store.AddWorkspace("Home", WorkspaceKind.Tasks);
        Assert.Equal(3, _store.Workspaces().Count);
        Assert.Equal("", _store.Workspaces().First(w => w.Value.Name == "Work").Value.Currency);

        // Once it is deleted, a new one can be made.
        _store.DeleteWorkspace(book);
        _store.AddWorkspace("Debts", WorkspaceKind.Debts, "USD");
        Assert.Equal("USD", _store.Workspaces().Single(w => w.Value.Kind == WorkspaceKind.Debts).Value.Currency);
    }

    [Fact]
    public void Debts_need_a_person_or_a_reason_and_sum_per_direction()
    {
        var book = _store.AddWorkspace("Debts", WorkspaceKind.Debts, "₫");
        Assert.Throws<ArgumentException>(() => _store.AddItem(book, new TrackerItemDraft("")));
        var tasks = _store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        Assert.Throws<ArgumentException>(() => _store.AddItem(tasks, new TrackerItemDraft("  ")));

        _store.AddItem(book, new TrackerItemDraft("Lunch", Person: "An", Amount: 150_000, Direction: DebtDirection.TheyOweMe));
        _store.AddItem(book, new TrackerItemDraft("", Person: "Binh", Amount: -50_000, Direction: DebtDirection.IOwe));
        var settled = _store.AddItem(book, new TrackerItemDraft("Taxi", Person: "An", Amount: 30_000));
        _store.Complete(settled);

        var totals = DebtTotals.From(_store.Items(book).Select(i => i.Value));
        Assert.Equal(150_000m, totals.OwedToMe);
        Assert.Equal(50_000m, totals.IOwe); // amounts are stored positive
        Assert.Equal(100_000m, totals.Net);
        Assert.Equal(2, totals.OpenCount);
    }

    [Theory]
    [InlineData("150000", 150000)]
    [InlineData("150,000", 150000)]
    [InlineData("1.500.000", 1500000)]
    [InlineData("150k", 150000)]
    [InlineData("1.5k", 1500)]
    [InlineData("2tr", 2000000)]
    [InlineData("2M", 2000000)]
    [InlineData("12,50", 12.5)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData(" 200 000 ", 200000)]
    public void Amounts_parse_the_way_people_type_them(string text, double expected)
    {
        Assert.True(TrackerFormat.TryParseAmount(text, out var amount));
        Assert.Equal((decimal)expected, amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("k")]
    [InlineData("1a2")]
    public void Nonsense_is_not_an_amount(string text) => Assert.False(TrackerFormat.TryParseAmount(text, out _));

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

        var ws = _store.AddWorkspace("Debts", WorkspaceKind.Debts);
        var id = _store.AddItem(ws, new TrackerItemDraft("Dinner, drinks", Person: "An", Amount: 1500.5m));
        var csv = TrackerCsv.Items(_store.AllItems(), _store.Workspaces().ToDictionary(w => w.Id, w => w.Value));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("workspace,kind,title,priority,status,", lines[0]);
        Assert.Contains("\"Dinner, drinks\"", lines[1]);
        Assert.Contains(",1500.5,TheyOweMe,", lines[1]);
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
        var debts = _store.AddWorkspace("Debts", WorkspaceKind.Debts, "₫");
        _store.AddItem(tasks, new TrackerItemDraft("later", TrackerPriority.Low));
        _store.AddItem(tasks, new TrackerItemDraft("now", TrackerPriority.Urgent, new DateOnly(2026, 9, 28)));
        _store.AddItem(debts, new TrackerItemDraft("", Person: "An", Amount: 20_000, Direction: DebtDirection.IOwe));

        var first = TrackerWidgetModel.Build(_store, "deleted-workspace", true, _time.Now, TimeZoneInfo.Utc);
        Assert.Equal(tasks, first.WorkspaceId);
        Assert.Equal(["now", "later"], first.Rows.Select(r => r.Title));
        Assert.Equal("Urgent · Due today", first.Rows[0].Details);

        Assert.Equal(debts, TrackerWidgetModel.NextWorkspace(_store, tasks));
        Assert.Equal(tasks, TrackerWidgetModel.NextWorkspace(_store, debts));
        var book = TrackerWidgetModel.Build(_store, debts, true, _time.Now, TimeZoneInfo.Utc);
        var row = Assert.Single(book.Rows);
        Assert.Equal("An", row.Title);
        Assert.False(row.OwedToMe);
        Assert.StartsWith("−", row.Amount);
    }

    [Fact]
    public void View_model_adds_completes_and_reports_bad_input()
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var settings = new SettingsStoreFactory(new HelmPaths(dir));
            var vm = new TrackerViewModel(_store, settings, new InlineDispatcher(),
                new YesDialogs(), new NullClipboard(), NullLogger<TrackerViewModel>.Instance);
            Assert.True(vm.HasNoWorkspaces);

            vm.CreateDebtWorkspaceCommand.Execute(null);
            Assert.True(vm.IsDebtWorkspace);
            vm.NewPerson = "An";
            vm.NewAmount = "lots";
            vm.AddCommand.Execute(null);
            Assert.True(vm.HasMessage);
            Assert.Empty(vm.OpenItems);

            vm.NewAmount = "150k";
            vm.AddCommand.Execute(null);
            var row = Assert.Single(vm.OpenItems);
            Assert.Equal(150_000m, row.Item.Amount);
            Assert.Equal("", vm.NewPerson);

            row.ToggleCompleteCommand.Execute(null);
            Assert.Empty(vm.OpenItems);
            Assert.Same(row, Assert.Single(vm.CompletedItems)); // the row instance is reused
            Assert.Equal("1", vm.ReportCompleted);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { } // a settings write may still be finishing; it is only a temp folder
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
                new YesDialogs(), new NullClipboard(), NullLogger<TrackerViewModel>.Instance);
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
