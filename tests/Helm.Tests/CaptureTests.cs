using Helm.Core.Capture;
using Helm.Core.Settings;
using Helm.Modules.QuickCapture;
using Helm.Modules.Tracker;

namespace Helm.Tests;

public sealed class CaptureTests
{
    // Tuesday 29 September 2026, 10:00 local.
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0);

    private static TaskCapture Task(string text) => TrackerCaptureParser.ParseTask(text, Now);

    [Theory]
    [InlineData("mua sữa mai 9h", "mua sữa", "2026-09-30", "09:00")]
    [InlineData("buy milk tomorrow 9am", "buy milk", "2026-09-30", "09:00")]
    [InlineData("họp 11h", "họp", "2026-09-29", "11:00")] // later today
    [InlineData("họp 9h", "họp", "2026-09-30", "09:00")] // 9:00 has passed: tomorrow
    [InlineData("báo cáo t2", "báo cáo", "2026-10-05", null)]
    [InlineData("báo cáo thứ 6", "báo cáo", "2026-10-02", null)]
    [InlineData("đá bóng chủ nhật 3h chiều", "đá bóng", "2026-10-04", "15:00")]
    [InlineData("nộp thuế 15/10", "nộp thuế", "2026-10-15", null)]
    [InlineData("gọi mẹ hôm nay 20:30", "gọi mẹ", "2026-09-29", "20:30")]
    [InlineData("call mom mon 9:30pm", "call mom", "2026-10-05", "21:30")]
    [InlineData("đi chơi mốt", "đi chơi", "2026-10-01", null)]
    [InlineData("demo tue", "demo", "2026-10-06", null)] // today is Tuesday: next week's
    public void Task_dates_and_times_are_read_at_the_end(string text, string title, string? day, string? time)
    {
        var task = Task(text);
        Assert.Equal(title, task.Title);
        Assert.Equal(day is null ? null : DateOnly.Parse(day), task.Day);
        Assert.Equal(time is null ? null : TimeSpan.Parse(time), task.Time);
    }

    [Theory]
    [InlineData("gọi cho Mai")] // a name
    [InlineData("sửa mái")] // "mái" (roof) is not "mai" (tomorrow)
    [InlineData("mua món")] // "món" is not Monday
    [InlineData("mua thêm một")] // "một" (one) is not "mốt"
    [InlineData("mai đi chợ")] // only at the end
    [InlineData("đọc 15 trang")] // a number is not a time
    public void Words_that_only_look_like_dates_stay_in_the_title(string text)
    {
        var task = Task(text);
        Assert.Equal(text, task.Title);
        Assert.Null(task.Day);
        Assert.Null(task.Time);
    }

    [Fact]
    public void Priority_and_list_can_be_anywhere()
    {
        var high = Task("fix bug ! #Work mai");
        Assert.Equal(("fix bug", TrackerPriority.High, "Work"), (high.Title, high.Priority, high.List));
        Assert.Equal(new DateOnly(2026, 9, 30), high.Day);
        Assert.Equal(TrackerPriority.Urgent, Task("!! deploy").Priority);
        Assert.Equal(TrackerPriority.Urgent, Task("deploy ! !!").Priority);
        Assert.Equal(TrackerPriority.Normal, Task("wow!").Priority);
    }

    [Theory]
    [InlineData("Nam 200k ăn trưa", "Nam", 200_000, DebtEntryKind.OwesMe, "ăn trưa")]
    [InlineData("Nam -200k", "Nam", 200_000, DebtEntryKind.IOwe, "")]
    [InlineData("Nam trả 50k", "Nam", 50_000, DebtEntryKind.Repayment, "")]
    [InlineData("nợ Nam 1tr", "Nam", 1_000_000, DebtEntryKind.IOwe, "")]
    [InlineData("Nam nợ 300k tiền điện", "Nam", 300_000, DebtEntryKind.OwesMe, "tiền điện")]
    [InlineData("Nguyễn Văn A 1.5tr tiền nhà", "Nguyễn Văn A", 1_500_000, DebtEntryKind.OwesMe, "tiền nhà")]
    [InlineData("An 150,000", "An", 150_000, DebtEntryKind.OwesMe, "")]
    public void Debts_read_person_amount_and_direction(string text, string person, int amount, DebtEntryKind kind, string note)
    {
        var debt = TrackerCaptureParser.ParseDebt(text);
        Assert.NotNull(debt);
        Assert.Equal((person, (decimal)amount, kind, note), (debt.Person, debt.Amount, debt.Kind, debt.Note));
    }

    [Theory]
    [InlineData("200k")]
    [InlineData("Nam")]
    [InlineData("Nam abc")]
    [InlineData("")]
    public void A_debt_needs_a_person_and_an_amount(string text) => Assert.Null(TrackerCaptureParser.ParseDebt(text));

    [Fact]
    public void Task_and_debt_targets_save_into_tracker()
    {
        using var dir = new TempDir();
        using var settings = new SettingsStoreFactory(new HelmPaths(dir.Path));
        var store = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>());
        var tasks = new TaskCaptureTarget(store, settings);
        var debts = new DebtCaptureTarget(store);

        Assert.Equal("Create a to-do list in Tracker first.", tasks.Preview("anything").Text);
        Assert.False(debts.Capture("Nam 200k").Saved);

        var todo = store.AddWorkspace("To-do", WorkspaceKind.Tasks);
        var work = store.AddWorkspace("Công việc", WorkspaceKind.Tasks);
        var book = store.AddWorkspace("Debts", WorkspaceKind.Debts);

        var preview = tasks.Preview("fix bug #cong !");
        Assert.True(preview.CanSave);
        Assert.StartsWith("Task in Công việc", preview.Text);
        Assert.Contains("High", preview.Text);
        Assert.True(tasks.Capture("fix bug #cong !").Saved);
        var item = Assert.Single(store.Items(work)).Value;
        Assert.Equal(("fix bug", TrackerPriority.High), (item.Title, item.Priority));

        Assert.True(tasks.Capture("water plants").Saved); // no #list: the first list
        Assert.Single(store.Items(todo));
        Assert.False(tasks.Preview("x #nowhere").CanSave);
        Assert.False(tasks.Preview("! #cong").CanSave); // no title left

        Assert.False(debts.Preview("Nam trả 50k").CanSave); // nothing to repay yet
        Assert.True(debts.Capture("Nam 200k lunch").Saved);
        var repay = debts.Preview("Nam trả 50k");
        Assert.True(repay.CanSave);
        Assert.StartsWith("Nam pays you back ", repay.Text);
        Assert.StartsWith("You owe Nam ", debts.Preview("Nam -10k").Text);
        Assert.True(debts.Capture("Nam trả 50k").Saved);
        Assert.Equal(150_000m, store.DebtBalance(book, "Nam"));
    }

    [Fact]
    public void A_prefix_picks_the_target_and_is_removed()
    {
        var note = new FakeTarget("note", "n");
        var task = new FakeTarget("task", "t");
        IReadOnlyList<ICaptureTarget> targets = [note, task];

        void Check(ICaptureTarget selected, string text, ICaptureTarget expectedTarget, string expectedText)
        {
            var (target, rest) = CaptureRouter.Resolve(targets, selected, text);
            Assert.Same(expectedTarget, target);
            Assert.Equal(expectedText, rest);
        }

        Check(note, "/t buy milk", task, "buy milk");
        Check(task, "  /n   hello", note, "hello");
        Check(task, "/N hello", note, "hello"); // any case
        Check(note, "/x unknown", note, "/x unknown"); // not a prefix: the text stays as typed
        Check(note, "a/t b", note, "a/t b");
        Assert.Equal([note], CaptureRouter.Available([task, note], id => id == "notes-module"));
    }

    [Fact]
    public void The_capture_box_follows_the_prefix_saves_and_keeps_text_that_failed()
    {
        using var dir = new TempDir();
        using var settings = new SettingsStoreFactory(new HelmPaths(dir.Path));
        var store = settings.Get<QuickCaptureSettings>("quick-capture");
        store.Update(s => s.DefaultTarget = "task");
        var note = new FakeTarget("note", "n");
        var task = new FakeTarget("task", "t") { Fail = true };
        var vm = new CaptureBoxViewModel(() => [note, task], store);
        string? saved = null;
        vm.Saved += (_, message) => saved = message;

        vm.Reset();
        Assert.Equal("task", vm.Selected?.Target.Id);
        vm.Text = "/n remember this";
        Assert.True(vm.Targets[0].IsActive);
        Assert.False(vm.Targets[1].IsActive);
        Assert.Equal("note: remember this", vm.PreviewText);
        Assert.True(vm.Save());
        Assert.Equal("saved remember this", saved);
        Assert.Equal("", vm.Text);
        Assert.Equal(["remember this"], note.Captured);

        vm.Text = "will fail";
        Assert.False(vm.Save());
        Assert.Equal("will fail", vm.Text); // nothing typed is lost
        Assert.True(vm.HasProblem);

        vm.Cycle(1);
        Assert.Equal("note", vm.Selected?.Target.Id);
        vm.Cycle(1);
        Assert.Equal("task", vm.Selected?.Target.Id);
        vm.SelectIndex(0);
        Assert.Equal("note", vm.Selected?.Target.Id);

        // From the palette: text and target given.
        vm.Reset("from the palette", "task");
        Assert.Equal(("from the palette", "task"), (vm.Text, vm.Selected?.Target.Id));
    }

    private sealed class FakeTarget(string id, string prefix) : ICaptureTarget
    {
        public List<string> Captured { get; } = [];
        public bool Fail { get; init; }
        public string Id => id;
        public string ModuleId => id == "note" ? "notes-module" : "tracker";
        public string Name => id;
        public string Prefix => prefix;
        public string Example => "";
        public int Order => id == "note" ? 0 : 1;

        public CapturePreview Preview(string text) => new(true, $"{id}: {text}");

        public CaptureResult Capture(string text)
        {
            if (Fail) return new CaptureResult(false, "no");
            Captured.Add(text);
            return new CaptureResult(true, "saved " + text);
        }
    }
}
