using Helm.Ai;
using Helm.Ai.Auto;
using Helm.Ai.Auto.Jobs;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Core.Text;
using Helm.Modules.Habits;
using Helm.Modules.Notes;
using Helm.Modules.Scratch;
using Helm.Modules.Tracker;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>Each automatic job: what it reads with Helm's tools, what it asks the (scripted) agent, what it publishes.</summary>
public sealed class AutoJobDataTests : IDisposable
{
    private readonly ManualTime _time = new(AutoKit.At(2026, 11, 3, 7, 5));
    private readonly MemorySettings<AiFeatureSettings> _features = new();
    private readonly MemorySynced<AiBrief> _briefs = new();
    private readonly AutoKit.Agent _agent = new();
    private readonly TrackerStore _tracker;
    private readonly HabitsStore _habits;
    private readonly WalletStore _wallet;
    private readonly FundBook _funds;
    private readonly NotesStore _notes;
    private readonly TempDir _dir = new();
    private readonly SyncDatabase _db;
    private readonly ScratchStore _scratch;

    public AutoJobDataTests()
    {
        _tracker = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
        _habits = new HabitsStore(new MemorySynced<Habit>(), new MemorySynced<HabitCheck>(), new MemorySynced<HabitLapse>(), _time, TimeZoneInfo.Local);
        _wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        _funds = new FundBook(new MemorySynced<WalletFund>(), _wallet);
        _notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        var paths = new HelmPaths(_dir.Path);
        _db = new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local);
        _scratch = new ScratchStore(new MemorySynced<ScratchItem>(), new BlobStore(_db, new NullBlobTransport(), paths.SyncBlobsDirectory, _time), _time);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private IMcpToolProvider[] Providers() =>
    [
        new TrackerMcpTools(_tracker), new HabitsMcpTools(_habits), new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), _wallet, _time), _wallet, _funds),
        new NotesMcpTools(_notes), new ScratchDigestTool(_scratch),
    ];

    /// <summary>Runs one job now, on, as the PC that runs them; the brief it published, if any.</summary>
    private async Task<AiBrief?> Run(IAutoJob job)
    {
        // On, from this very minute.
        var at = TimeZoneInfo.ConvertTime(_time.Now, TimeZoneInfo.Local).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        _features.Update(s => s.Features[job.Feature] = new AiFeatureChoice { Route = AiRoutePolicy.PcOnly, At = at });
        var scheduler = new AutoJobScheduler([job], _features, new AutoJobRuns(new MemorySynced<AutoJobRun>()), new MemorySynced<Helm.Ai.Assistant.AiWorkerRecord>(),
            new AutoJobAsker(_agent, null, [], new AiUsageBook(new MemorySyncedLog<AiUsageEntry>(), _time)), new AutoJobTools(Providers(), _ => true),
            new AiBriefBook(_briefs, _time), () => "pc", "PC-HUNG", () => false, _time, TimeZoneInfo.Local);
        _briefs.All().ToList().ForEach(b => _briefs.Delete(b.Id));
        await scheduler.TickAsync(default);
        return _briefs.All().SingleOrDefault()?.Value;
    }

    private string Asked => Assert.Single(_agent.Jobs).Text;

    private static DateOnly Day(int month, int day) => new(2026, month, day);

    [Fact]
    public async Task The_morning_brief_tells_todays_tasks_the_habits_to_do_and_yesterdays_spending()
    {
        var list = _tracker.AddWorkspace("Việc");
        _tracker.AddItem(list, new TrackerItemDraft("Gọi khách Nam", TrackerPriority.High, Day(11, 3)));
        _tracker.AddItem(list, new TrackerItemDraft("Nộp báo cáo", DueDate: Day(11, 20)));
        _habits.Add("Chạy bộ");
        var food = _wallet.AddCategory("Ăn uống", CategoryKind.Expense);
        _wallet.AddManual(-45000m, "Phở", AutoKit.At(2026, 11, 2, 12, 0), food);

        var brief = await Run(new MorningBriefJob());

        Assert.Equal("Bản tin sáng · Thứ Ba 03/11", brief!.Title);
        Assert.Equal(AiFeatureIds.MorningBrief, brief.Kind);
        Assert.Contains("Gọi khách Nam", Asked);
        Assert.DoesNotContain("Nộp báo cáo", Asked);
        Assert.Contains("Chạy bộ", Asked);
        Assert.Contains("\"spent\":45000", Asked);
        Assert.Contains("Ăn uống", Asked);
    }

    [Fact]
    public async Task A_morning_with_nothing_in_it_asks_nobody()
    {
        Assert.Null(await Run(new MorningBriefJob()));
        Assert.Empty(_agent.Jobs);
    }

    [Fact]
    public async Task The_evening_review_tells_what_was_done_and_what_slips_to_tomorrow()
    {
        _time.Now = AutoKit.At(2026, 11, 3, 21, 35);
        var list = _tracker.AddWorkspace("Việc");
        var done = _tracker.AddItem(list, new TrackerItemDraft("Viết spec", DueDate: Day(11, 3)));
        _tracker.Complete(done);
        _tracker.AddItem(list, new TrackerItemDraft("Sửa xe", DueDate: Day(11, 2)));
        _tracker.AddItem(list, new TrackerItemDraft("Họp team", DueDate: Day(11, 4)));

        var brief = await Run(new EveningReviewJob());

        Assert.StartsWith("Tổng kết tối", brief!.Title);
        var asked = Asked;
        Assert.Contains("Done today: [{\"title\":\"Viết spec\"", asked);
        Assert.Contains("still open: [{\"title\":\"Sửa xe\"", asked);
        Assert.Contains("Due tomorrow: [{\"title\":\"Họp team\"", asked);
        Assert.Contains("ONE concrete suggestion", asked);
    }

    [Fact]
    public async Task Habit_coaching_reads_the_reasons_of_a_habit_skipped_two_days_in_a_row()
    {
        _time.Now = AutoKit.At(2026, 10, 27, 8, 0);
        var run = _habits.Add("Chạy bộ")!;
        _habits.Add("Đọc sách");
        _time.Now = AutoKit.At(2026, 11, 3, 19, 5);
        for (var d = Day(10, 27); d <= Day(10, 31); d = d.AddDays(1)) _habits.Check(run, d);
        _habits.WriteReason(run, Day(11, 1), Day(11, 2), "Mưa to, lười ra đường");
        var reader = _habits.Habits().Single(h => h.Value.Title == "Đọc sách").Id;
        for (var d = Day(10, 27); d <= Day(11, 2); d = d.AddDays(1)) _habits.Check(reader, d);

        var brief = await Run(new HabitCoachJob());

        Assert.Equal("Thói quen: Chạy bộ lỡ 2 ngày", brief!.Title);
        Assert.Contains("Mưa to, lười ra đường", Asked);
        Assert.DoesNotContain("Đọc sách", Asked);
    }

    [Fact]
    public async Task The_weekly_spending_digest_compares_with_last_week_and_names_the_funds_running_low()
    {
        _time.Now = AutoKit.At(2026, 11, 8, 20, 5);
        var food = _wallet.AddCategory("Ăn uống", CategoryKind.Expense);
        var fuel = _wallet.AddCategory("Xăng xe", CategoryKind.Expense);
        _wallet.AddManual(-900000m, "Nhà hàng", AutoKit.At(2026, 11, 4, 19, 0), food);
        _wallet.AddManual(-100000m, "Xăng", AutoKit.At(2026, 11, 5, 8, 0), fuel);
        _wallet.AddManual(-300000m, "Phở", AutoKit.At(2026, 10, 28, 12, 0), food);
        _funds.Add("Ăn uống", 1_000_000m, true, new DateOnly(2026, 11, 1), [food]);

        var brief = await Run(new SpendingDigestJob());

        Assert.Equal("Chi tiêu tuần · 02/11–08/11", brief!.Title);
        var asked = Asked;
        Assert.Contains("This period: {\"from\":\"2026-11-02\",\"to\":\"2026-11-08\",\"spent\":1000000", asked);
        Assert.Contains("The period before: {\"from\":\"2026-10-26\",\"to\":\"2026-11-01\",\"spent\":300000", asked);
        Assert.Contains("\"fund\":\"Ăn uống\"", asked);
    }

    [Fact]
    public async Task Important_days_remind_three_days_before_and_on_the_day_with_lunar_giỗ()
    {
        _notes.Add(ImportantDays.NoteTitle, "06/11 · Sinh nhật mẹ\n15/09 âm · Giỗ ông nội\n03/11/2026 · Đăng kiểm xe\nghi chú khác");
        _time.Now = AutoKit.At(2026, 11, 3, 8, 5);

        var brief = await Run(new ImportantDaysJob());

        Assert.Equal("Ngày quan trọng · 2 ngày sắp tới", brief!.Title);
        var asked = Asked;
        Assert.Contains("\"what\":\"Sinh nhật mẹ\",\"date\":\"2026-11-06\",\"inDays\":3", asked);
        Assert.Contains("\"what\":\"Đăng kiểm xe\",\"date\":\"2026-11-03\",\"inDays\":0", asked);
        Assert.DoesNotContain("Giỗ ông nội", asked);

        // 15/09 âm lịch 2026 is 24 October; next year's is 14 October 2027.
        Assert.Equal(new DateOnly(2027, 10, 14), ImportantDays.ParseLine("15/09 âm · Giỗ ông nội")!.Next(Day(11, 3)));
    }

    [Fact]
    public async Task New_untitled_notes_get_a_title_and_tags_that_Ask_Helm_can_take_back()
    {
        _time.Now = AutoKit.At(2026, 11, 3, 9, 0);
        var id = _notes.Add("", "mua quà sinh nhật cho mẹ: khăn lụa, hoa\nđặt bàn tối thứ 7");
        var titled = _notes.Add("Đã có tên", "nội dung");
        _time.Now = AutoKit.At(2026, 11, 3, 10, 5);
        _agent.Answer = job => $$"""Đây: [{"id":"{{id}}","title":"Quà sinh nhật mẹ","tags":["gia đình","Quà"]}]""";

        var brief = await Run(new NoteTitlesJob());

        Assert.DoesNotContain(titled, Asked);
        Assert.Contains("khăn lụa", Asked);
        var note = _notes.Get(id)!;
        Assert.Equal("Quà sinh nhật mẹ", note.Title);
        Assert.EndsWith("\n#gia-đình #quà", note.Body);
        Assert.Equal("Em đặt tiêu đề cho 1 ghi chú", brief!.Title);
        Assert.Contains("“Quà sinh nhật mẹ” #gia-đình #quà", brief.Text);

        // The next hour, the same note is not asked about again.
        _agent.Jobs.Clear();
        _time.Now = AutoKit.At(2026, 11, 3, 11, 5);
        Assert.Null(await Run(new NoteTitlesJob()));
        Assert.Empty(_agent.Jobs);
    }

    [Fact]
    public async Task Undoing_a_briefs_change_gives_the_note_back_as_it_was()
    {
        _time.Now = AutoKit.At(2026, 11, 3, 9, 0);
        var id = _notes.Add("", "danh sách đồ đi Đà Lạt");
        _time.Now = AutoKit.At(2026, 11, 3, 10, 5);
        _agent.Answer = _ => $$"""[{"id":"{{id}}","title":"Đồ đi Đà Lạt","tags":["du lịch"]}]""";
        Assert.NotNull(await Run(new NoteTitlesJob()));
        var briefId = Assert.Single(_briefs.All()).Id;

        var tools = new AiBriefTools(new AiBriefBook(_briefs, _time), _features, Providers);
        await AutoKit.Run(tools, "ai_brief_undo", new { id = briefId });

        Assert.Equal("", _notes.Get(id)!.Title);
        Assert.Equal("danh sách đồ đi Đà Lạt", _notes.Get(id)!.Body);
        Assert.NotNull(_briefs.Get(briefId)!.UndoneAt);
    }

    [Fact]
    public async Task Scratch_suggestions_name_things_but_never_a_code_and_move_nothing()
    {
        _time.Now = AutoKit.At(2026, 11, 7, 10, 5);
        await _scratch.AddTextAsync("https://example.com/bai-viet-hay", "Phone");
        await _scratch.AddTextAsync("Xk92mPq7Zr4T", "Phone");
        await _scratch.AddTextAsync("địa chỉ quán cà phê mới ở quận 3", "Phone");

        var brief = await Run(new ScratchTidyJob());

        Assert.Equal("Gợi ý dọn Scratch · 3 món", brief!.Title);
        Assert.Contains("example.com", Asked);
        Assert.Contains("quán cà phê", Asked);
        Assert.DoesNotContain("Xk92mPq7Zr4T", Asked);
        Assert.Equal(3, _scratch.Items(ScratchFilter.All).Count);
    }

    [Theory]
    [InlineData(2026, 2, 17, 1, 1, 2026)]
    [InlineData(2027, 2, 6, 1, 1, 2027)]
    [InlineData(2025, 1, 29, 1, 1, 2025)]
    [InlineData(2026, 9, 25, 15, 8, 2026)]
    [InlineData(2025, 10, 6, 15, 8, 2025)]
    public void The_lunar_calendar_knows_Tet_and_mid_autumn(int y, int m, int d, int lunarDay, int lunarMonth, int lunarYear)
    {
        var solar = new DateOnly(y, m, d);
        var lunar = LunarCalendar.ToLunar(solar);
        Assert.Equal((lunarDay, lunarMonth, lunarYear), (lunar.Day, lunar.Month, lunar.Year));
        Assert.Equal(solar, LunarCalendar.ToSolar(lunar));
    }

    [Fact]
    public async Task Ask_Helm_adds_an_important_day_to_its_note_and_takes_it_back()
    {
        var notes = new NotesMcpTools(_notes);
        var add = notes.Tools.Single(t => t.Name == "notes_add_important_day");

        var first = await AutoKit.Run(notes, "notes_add_important_day", new { label = "Giỗ bà ngoại", date = "10/07", lunar = true });
        var second = await AutoKit.Run(notes, "notes_add_important_day", new { label = "Gia hạn tên miền", date = "2027-03-15" });

        var note = Assert.Single(_notes.Notes()).Value;
        Assert.Equal(ImportantDays.NoteTitle, note.Title);
        Assert.Equal("10/07 âm · Giỗ bà ngoại\n15/03/2027 · Gia hạn tên miền", note.Body);
        Assert.Equal(2, ImportantDays.Parse(note.Body).Count);

        await add.Undo!(second, default);
        Assert.Equal("10/07 âm · Giỗ bà ngoại", _notes.Notes()[0].Value.Body);
        await add.Undo!(first, default);
        Assert.Empty(_notes.Notes());
    }
}
