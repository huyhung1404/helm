using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Ai.Auto;
using Helm.Core.Mcp;

namespace Helm.Tests;

/// <summary>The PC's automatic jobs: when they run, once per period, on one PC, never on the paid API by themselves.</summary>
public sealed class AutoJobsTests
{
    private readonly ManualTime _time = new(AutoKit.At(2026, 11, 3, 6, 50));
    private readonly MemorySettings<AiFeatureSettings> _features = new();
    private readonly MemorySynced<AutoJobRun> _runs = new();
    private readonly MemorySynced<AiWorkerRecord> _workers = new();
    private readonly MemorySynced<AiBrief> _briefs = new();
    private readonly MemorySyncedLog<AiUsageEntry> _usage = new();
    private readonly MemorySynced<AiKey> _keys = new();
    private readonly AutoKit.Agent _agent = new();
    private readonly AutoKit.ApiFactory _api = new();
    private bool _vault;

    private AutoJobScheduler Scheduler(IAutoJob job, string self = "pc-b", IAgentRoute? agent = null) =>
        new([job], _features, new AutoJobRuns(_runs), _workers,
            new AutoJobAsker(agent ?? _agent, new AiKeyStore(_keys), [_api], new AiUsageBook(_usage, _time)),
            new AutoJobTools([], _ => true), new AiBriefBook(_briefs, _time), () => self, "PC-HUNG", () => _vault, _time, TimeZoneInfo.Local);

    [Theory]
    [InlineData(6, 59, null)]
    [InlineData(7, 0, "2026-11-03")]
    [InlineData(10, 59, "2026-11-03")]
    [InlineData(11, 0, null)]
    public void A_daily_slot_is_due_from_its_time_for_a_few_hours(int hour, int minute, string? period) =>
        Assert.Equal(period, new AutoSlot("day", AutoEvery.Day).PeriodAt(new DateTime(2026, 11, 3, hour, minute, 0), new TimeOnly(7, 0)));

    [Fact]
    public void Slots_keep_their_weekday_their_day_of_the_month_and_the_evening_past_midnight()
    {
        var evening = new AutoSlot("day", AutoEvery.Day);
        Assert.Equal("2026-11-03", evening.PeriodAt(new DateTime(2026, 11, 4, 0, 30, 0), new TimeOnly(21, 30)));
        var sunday = new AutoSlot("week", AutoEvery.Week, DayOfWeek.Sunday);
        Assert.Null(sunday.PeriodAt(new DateTime(2026, 11, 7, 20, 5, 0), new TimeOnly(20, 0)));
        Assert.Equal("2026-11-08", sunday.PeriodAt(new DateTime(2026, 11, 8, 20, 5, 0), new TimeOnly(20, 0)));
        var first = new AutoSlot("month", AutoEvery.Month, MonthDay: 1);
        Assert.Equal("2026-12-01", first.PeriodAt(new DateTime(2026, 12, 1, 20, 0, 0), new TimeOnly(20, 0)));
        Assert.Null(first.PeriodAt(new DateTime(2026, 12, 2, 20, 0, 0), new TimeOnly(20, 0)));
        Assert.Equal("2026-11-03T14", new AutoSlot("hour", AutoEvery.Hour).PeriodAt(new DateTime(2026, 11, 3, 14, 59, 0), TimeOnly.MinValue));
    }

    [Fact]
    public async Task A_job_runs_at_its_time_once_per_period_even_after_a_restart()
    {
        var job = new AutoKit.Job(AiFeatureIds.MorningBrief);

        Assert.Equal(0, await Scheduler(job).TickAsync(default));
        _time.Now = AutoKit.At(2026, 11, 3, 7, 1);
        Assert.Equal(1, await Scheduler(job).TickAsync(default));
        _time.Advance(TimeSpan.FromMinutes(1));
        // A new scheduler is Helm started again: the synced run says it is done.
        Assert.Equal(0, await Scheduler(job).TickAsync(default));

        var brief = Assert.Single(_briefs.All()).Value;
        Assert.Equal(AiFeatureIds.MorningBrief, brief.Kind);
        Assert.Equal("Dạ, bản tin.", brief.Text);
        Assert.Equal("PC-HUNG", brief.Device);
        Assert.Equal(AiUsageBook.AgentRoute, Assert.Single(_usage.All()).Value.Route);

        _time.Now = AutoKit.At(2026, 11, 4, 7, 30);
        Assert.Equal(1, await Scheduler(job).TickAsync(default));
        Assert.Equal(2, _briefs.All().Count);
    }

    [Fact]
    public async Task Its_own_time_and_off_are_the_features_choice()
    {
        var job = new AutoKit.Job(AiFeatureIds.MorningBrief);
        _features.Update(s => s.Features[AiFeatureIds.MorningBrief] = new AiFeatureChoice { Route = AiRoutePolicy.PcOnly, At = "06:30" });
        Assert.Equal(1, await Scheduler(job).TickAsync(default));

        Assert.False(new AiFeatureSettings().For(AiFeatureIds.NoteTitles).Enabled);
        Assert.True(new AiFeatureSettings().For(AiFeatureIds.SpendingDigest).Enabled);
        Assert.Equal(AiRoutePolicy.PcOnly, new AiFeatureSettings().For(AiFeatureIds.EveningReview).Route);
        _time.Now = AutoKit.At(2026, 11, 3, 12, 0);
        Assert.Equal(0, await Scheduler(new AutoKit.Job(AiFeatureIds.NoteTitles, AutoEvery.Hour)).TickAsync(default));
    }

    [Fact]
    public async Task Without_an_agent_nothing_goes_to_the_paid_API_and_it_runs_once_an_agent_is_ready()
    {
        new AiKeyStore(_keys).Add(AiProviders.Claude, "Helm", "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa", null);
        var job = new AutoKit.Job(AiFeatureIds.MorningBrief);
        _time.Now = AutoKit.At(2026, 11, 3, 7, 5);
        _agent.Problem = "Claude Code: not signed in.";

        Assert.Equal(0, await Scheduler(job).TickAsync(default));
        Assert.Equal(0, _api.Created);
        Assert.Empty(_briefs.All());
        Assert.Empty(_runs.All());

        // Later in the window the agent is back.
        _time.Advance(TimeSpan.FromMinutes(40));
        _agent.Problem = null;
        Assert.Equal(1, await Scheduler(job).TickAsync(default));
        Assert.Single(_briefs.All());
        Assert.Equal(0, _api.Created);
    }

    [Fact]
    public async Task The_API_is_used_only_where_the_user_allowed_it()
    {
        new AiKeyStore(_keys).Add(AiProviders.Claude, "Helm", "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa", null);
        _features.Update(s => s.Features[AiFeatureIds.MorningBrief] = new AiFeatureChoice { Route = AiRoutePolicy.PreferPc });
        _time.Now = AutoKit.At(2026, 11, 3, 7, 5);
        _agent.Problem = "No agent is set up on this PC.";

        Assert.Equal(1, await Scheduler(new AutoKit.Job(AiFeatureIds.MorningBrief)).TickAsync(default));

        Assert.Equal(1, _api.Created);
        Assert.Equal("Từ API.", Assert.Single(_briefs.All()).Value.Text);
        Assert.Equal(AiUsageBook.ApiRoute, Assert.Single(_usage.All()).Value.Route);
    }

    [Fact]
    public async Task Of_two_PCs_with_agents_only_the_one_with_the_lower_id_runs_them()
    {
        _time.Now = AutoKit.At(2026, 11, 3, 7, 5);
        _workers.Upsert("pc-a", new AiWorkerRecord { Device = "PC-A", Agents = ["Codex"], SeenAt = _time.Now });
        var job = new AutoKit.Job(AiFeatureIds.MorningBrief);

        Assert.Equal(0, await Scheduler(job, self: "pc-b").TickAsync(default));
        // An offline PC, or one without an agent ready, does not count.
        _workers.Upsert("pc-a", new AiWorkerRecord { Device = "PC-A", Agents = [], Problem = "No agent", SeenAt = _time.Now });
        Assert.Equal(1, await Scheduler(job, self: "pc-b").TickAsync(default));
    }

    [Fact]
    public async Task Nothing_runs_while_a_Vault_is_unlocked()
    {
        _time.Now = AutoKit.At(2026, 11, 3, 7, 5);
        _vault = true;
        Assert.Equal(0, await Scheduler(new AutoKit.Job(AiFeatureIds.MorningBrief)).TickAsync(default));
        Assert.Empty(_agent.Jobs);
        _vault = false;
        Assert.Equal(1, await Scheduler(new AutoKit.Job(AiFeatureIds.MorningBrief)).TickAsync(default));
    }

    [Fact]
    public async Task A_failed_run_is_tried_again_later_in_the_window_a_few_times()
    {
        _time.Now = AutoKit.At(2026, 11, 3, 7, 5);
        _agent.Fail = true;
        var job = new AutoKit.Job(AiFeatureIds.MorningBrief);

        Assert.Equal(1, await Scheduler(job).TickAsync(default));
        var run = Assert.Single(_runs.All()).Value;
        Assert.Equal(AutoJobRuns.Failed, run.State);
        Assert.Empty(_briefs.All());

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await Scheduler(job).TickAsync(default));
        _time.Advance(TimeSpan.FromMinutes(15));
        _agent.Fail = false;
        Assert.Equal(1, await Scheduler(job).TickAsync(default));
        Assert.Equal(AutoJobRuns.Done, Assert.Single(_runs.All()).Value.State);
        Assert.Equal(2, Assert.Single(_runs.All()).Value.Attempts);
        Assert.Single(_briefs.All());
    }

    [Fact]
    public async Task An_agent_job_is_text_only_with_the_briefs_instructions_and_ends()
    {
        _time.Now = AutoKit.At(2026, 11, 3, 7, 5);
        await Scheduler(new AutoKit.Job(AiFeatureIds.MorningBrief)).TickAsync(default);

        var sent = Assert.Single(_agent.Jobs);
        Assert.Empty(sent.Tools);
        Assert.Empty(sent.PhoneTools);
        Assert.Contains("Vietnamese", sent.System);
        Assert.Equal([sent.ConversationId], _agent.Ended);
    }

    [Fact]
    public void Each_device_tells_of_a_new_brief_once_and_not_of_old_or_read_ones()
    {
        using var dir = new TempDir();
        using var settings = new Helm.Core.Settings.SettingsStoreFactory(new Helm.Core.Settings.HelmPaths(dir.Path));
        var book = new AiBriefBook(_briefs, _time);
        book.Publish(AiFeatureIds.MorningBrief, "Old", "Before Helm started here.");
        _time.Advance(TimeSpan.FromMinutes(1));
        var alerts = new AiBriefAlerts(book, settings.Get<AiBriefAlertState>(AiBriefAlertState.StoreId));
        var shown = new List<string>();
        alerts.Start(b => shown.Add(b.Value.Title));

        book.Publish(AiFeatureIds.MorningBrief, "Bản tin sáng", "Hôm nay Anh có 2 việc.");
        // Read on the phone before it reached this PC.
        _briefs.SetRemote("read", new AiBrief { Kind = AiFeatureIds.EveningReview, Title = "Read", Text = "Already read.", CreatedAt = _time.Now, ReadAt = _time.Now });
        Assert.Equal(["Bản tin sáng"], shown);

        // Helm started again on this device: nothing is told twice.
        Assert.Empty(new AiBriefAlerts(book, settings.Get<AiBriefAlertState>(AiBriefAlertState.StoreId)).TakeNew());
    }

    [Fact]
    public async Task Ask_Helm_reads_the_briefs_and_sets_a_jobs_time_with_undo()
    {
        var book = new AiBriefBook(_briefs, _time);
        var id = book.Publish(AiFeatureIds.MorningBrief, "Bản tin sáng", "Hôm nay Anh có 2 việc.");
        var tools = new AiBriefTools(book, _features, () => []);

        var read = await AutoKit.Run(tools, "ai_briefs", new { kind = AiFeatureIds.MorningBrief });
        Assert.Equal("Hôm nay Anh có 2 việc.", read[0].GetProperty("text").GetString());
        Assert.NotNull(book.Get(id)!.ReadAt);

        var set = tools.Tools.Single(t => t.Name == "ai_auto_job_set");
        var result = await AutoKit.Run(tools, "ai_auto_job_set", new { job = AiFeatureIds.HabitCoach, on = true, at = "18:00" });
        Assert.True(_features.Current.For(AiFeatureIds.HabitCoach).Enabled);
        Assert.Equal("18:00", _features.Current.For(AiFeatureIds.HabitCoach).At);
        await set.Undo!(result, default);
        Assert.False(_features.Current.For(AiFeatureIds.HabitCoach).Enabled);
        Assert.Null(_features.Current.For(AiFeatureIds.HabitCoach).At);

        Assert.Contains("ai_briefs", HelmAssistant.Tools);
        Assert.Contains("ai", ToolScope.GroupsFor("bản tin sáng nay nói gì?"));
        Assert.Contains("notes", ToolScope.GroupsFor("nhớ giúp anh sinh nhật mẹ 15/3"));
    }
}

/// <summary>The doubles the automatic jobs' tests share.</summary>
internal static class AutoKit
{
    /// <summary>A moment of the user's local time.</summary>
    public static DateTimeOffset At(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    public static async Task<System.Text.Json.JsonElement> Run(IMcpToolProvider provider, string tool, object args)
    {
        var result = await provider.Tools.Single(t => t.Name == tool).Run(System.Text.Json.JsonSerializer.SerializeToElement(args), default);
        return System.Text.Json.JsonSerializer.SerializeToElement(result);
    }

    /// <summary>A job that asks the agent one thing.</summary>
    public sealed class Job(string feature, AutoEvery every = AutoEvery.Day) : IAutoJob
    {
        public string Feature => feature;

        public IReadOnlyList<AutoSlot> Slots { get; } = [new("slot", every)];

        public async Task<AutoBrief?> RunAsync(AutoJobContext context, CancellationToken ct) =>
            new AutoBrief("Brief", await context.AskAsync("Write it.", ct));
    }

    /// <summary>This PC's agents, scripted: what they were asked, and an answer.</summary>
    public sealed class Agent : IAgentRoute
    {
        public string? Problem { get; set; }
        public bool Fail { get; set; }
        public Func<AiJob, string> Answer { get; set; } = _ => "Dạ, bản tin.";
        public List<AiJob> Jobs { get; } = [];
        public List<string> Ended { get; } = [];

        public string Device => "PC-HUNG";

        public string? Unavailable() => Problem;

        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            Jobs.Add(job);
            if (Fail) throw new AiRouteException("Claude Code stopped.");
            return Task.FromResult(new AiJobResult(Answer(job), [], [], 0.002m) { Agent = "Claude Code" });
        }

        public Task UndoAsync(AiJobAction action, CancellationToken ct) => Task.CompletedTask;

        public void End(string conversationId) => Ended.Add(conversationId);
    }

    /// <summary>The paid API: counts the chats opened.</summary>
    public sealed class ApiFactory : IAssistantChatFactory
    {
        public int Created { get; private set; }

        public AiProvider Provider => AiProviders.Claude;

        public IAssistantChat Create(string apiKey, string model, string effort, string system, IReadOnlyList<AssistantTool> tools)
        {
            Created++;
            return new Chat();
        }

        private sealed class Chat : IAssistantChat
        {
            public void AddUser(string text) { }

            public Task<AssistantStep> NextAsync(CancellationToken ct) => Task.FromResult(new AssistantStep("Từ API.", [], new AssistantUsage(1000, 100, 0, 0), false));

            public void AddResults(IReadOnlyList<ToolOutcome> results) { }
        }
    }
}
