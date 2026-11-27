using System.Text.Json;
using Helm.Ai;
using Helm.Ai.Assistant;
using Helm.Core.Ai;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Tracker;
using Helm.Modules.Wallet;
using static Helm.Tests.ChatScript;

namespace Helm.Tests;

/// <summary>
/// The pre-release review's findings on the router: two PCs online, a task done before, unpriced models and the cap,
/// the old management key, the PC's agent list, the live channel, and the one text call.
/// </summary>
public sealed class AiReviewFixesTests : IDisposable
{
    private readonly MemorySynced<AiJobRecord> _jobs = new();
    private readonly MemorySynced<AiWorkerRecord> _workers = new();
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ---- 1. Two PCs: a conversation stays where it began, End goes where it ran ------------------------------------

    [Fact]
    public async Task With_two_PCs_a_conversation_stays_on_its_PC_and_End_goes_where_it_ran()
    {
        var a = new Pc("PC-A");
        var b = new Pc("PC-B");
        using var onA = new AgentJobWorker(_jobs, _workers, a, () => "pc-a", "PC-A", () => ["Codex"]);
        using var onB = new AgentJobWorker(_jobs, _workers, b, () => "pc-b", "PC-B", () => ["Codex"]);
        onA.Start();
        onB.Start();
        Seen("pc-a", "PC-A", 2);
        Seen("pc-b", "PC-B", 1);
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");

        var first = await phone.RunAsync(Job("conv-1"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("PC-B", first.Device);
        // PC-A is heard from last now: still, conv-1's next turn goes to PC-B, where its chat is.
        Seen("pc-a", "PC-A", 0);
        await phone.RunAsync(Job("conv-1"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(2, b.Jobs.Count);
        // A new conversation goes to the best PC now.
        var other = await phone.RunAsync(Job("conv-2"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("PC-A", other.Device);
        Assert.Single(a.Jobs);

        phone.End("conv-1");
        await Eventually(() => b.Ended.Contains("conv-1"));
        Assert.DoesNotContain("conv-1", a.Ended);
    }

    [Fact]
    public async Task A_conversation_whose_PC_is_gone_gives_way_instead_of_starting_over_on_another()
    {
        var b = new Pc("PC-B");
        using var onB = new AgentJobWorker(_jobs, _workers, b, () => "pc-b", "PC-B", () => ["Codex"]);
        onB.Start();
        var phone = new SyncAgentRoute(_jobs, _workers, () => "phone-id");
        await phone.RunAsync(Job("conv-1"), TimeSpan.FromSeconds(5), CancellationToken.None);

        onB.Dispose();
        Seen("pc-a", "PC-A", 0);

        await Assert.ThrowsAsync<AiRouteException>(() => phone.RunAsync(Job("conv-1"), TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    // ---- 4. A task done before ------------------------------------------------------------------------------------

    [Fact]
    public async Task Ticking_a_task_already_done_changes_nothing_and_its_Undo_never_reopens_it()
    {
        var tracker = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>());
        var list = tracker.AddWorkspace("Việc");
        var id = tracker.AddItem(list, new TrackerItemDraft("Gọi khách"));
        tracker.Complete(id);
        var tools = new TrackerMcpTools(tracker);
        var complete = tools.Tools.First(t => t.Name == "tracker_complete_task");

        var json = Serialize(await complete.Run(Args(new { id }), CancellationToken.None));
        Assert.True(McpTool.ChangedNothing(json));
        await complete.Undo!(JsonDocument.Parse(json).RootElement, CancellationToken.None);
        Assert.True(tracker.GetItem(id)!.IsCompleted);

        // One it did tick goes back.
        tracker.Reopen(id);
        var ticked = Serialize(await complete.Run(Args(new { id }), CancellationToken.None));
        Assert.False(McpTool.ChangedNothing(ticked));
        await complete.Undo!(JsonDocument.Parse(ticked).RootElement, CancellationToken.None);
        Assert.False(tracker.GetItem(id)!.IsCompleted);
    }

    // ---- 5. A model with no price still counts against the cap ----------------------------------------------------

    [Fact]
    public void A_model_with_no_price_counts_at_its_providers_dearest_against_the_cap()
    {
        var usage = new AiUsageBook(new MemorySyncedLog<AiUsageEntry>());
        var entry = usage.Record(AiFeatureIds.Default, AiProviders.Grok, "grok-9-preview", "k", 1_000_000, 0, 0, 0);
        Assert.Null(entry.Usd);

        Assert.Equal(AiPrices.Ceiling(AiProviders.Grok.Id).Input, usage.SpentToday(AiFeatureIds.Default));
        Assert.Equal(AiPrices.For("claude-fable-5-1"), AiPrices.Ceiling(AiProviders.Claude.Id));
        usage.Record(AiFeatureIds.Default, AiProviders.Claude, "claude-next", "k", 0, 100_000, 0, 0);
        Assert.Equal(2m + 5m, usage.SpentToday(AiFeatureIds.Default));
    }

    // ---- 7. The old management key waits for the first pull -------------------------------------------------------

    [Fact]
    public void An_old_management_key_moves_only_after_the_first_pull_and_never_over_a_synced_one()
    {
        var paths = new HelmPaths(_dir.Path);
        var protector = new PlainSecretProtector();
        var old = Path.Combine(paths.ModuleDataDirectory("ai"), "management-xai.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllBytes(old, protector.Protect(System.Text.Encoding.UTF8.GetBytes("xai-old"), "helm.ai.management.xai"));
        var written = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(old, written.UtcDateTime);
        var records = new MemorySynced<AiManagementKey>();
        var sync = new FakeSync();

        var keys = new AiManagementKeys(records, paths, protector, sync: sync);

        // Before the first pull: used here, not written to sync, the file kept.
        Assert.Equal("xai-old", keys.Get(AiProviders.Grok));
        Assert.Null(records.Get("xai"));
        Assert.True(File.Exists(old));

        // The phone's newer key arrives with the first pull: it stays.
        records.SetRemote("xai", new AiManagementKey { Provider = "xai", Secret = "xai-phone", SavedAt = written.AddDays(30) });
        sync.Pulled();
        Assert.Equal("xai-phone", keys.Get(AiProviders.Grok));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void An_old_management_key_with_nothing_synced_moves_with_its_files_time()
    {
        var paths = new HelmPaths(_dir.Path);
        var protector = new PlainSecretProtector();
        var old = Path.Combine(paths.ModuleDataDirectory("ai"), "management-xai.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllBytes(old, protector.Protect(System.Text.Encoding.UTF8.GetBytes("xai-old"), "helm.ai.management.xai"));
        var written = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(old, written.UtcDateTime);
        var records = new MemorySynced<AiManagementKey>();
        var sync = new FakeSync();
        _ = new AiManagementKeys(records, paths, protector, sync: sync);

        sync.Pulled();

        Assert.Equal("xai-old", records.Get("xai")!.Secret);
        Assert.Equal(written, records.Get("xai")!.SavedAt);
        Assert.False(File.Exists(old));
    }

    // ---- 8. The PC's agent list changes without stopping what runs -------------------------------------------------

    [Fact]
    public void A_rename_or_a_new_order_keeps_every_agent_and_only_one_turned_off_goes()
    {
        var made = 0;
        Agent Make(string id) { made++; return new Agent(id); }
        var (now, gone) = AgentRoster.Follow<(string Id, string Kind), Agent>([], [("a", "codex"), ("b", "claude")],
            (x, p) => x.Id == p.Id, (x, _) => x, p => Make(p.Id));
        Assert.Equal(2, made);
        var a = now[0];
        var b = now[1];

        // Reordered: the same two, nothing made or gone.
        (now, gone) = AgentRoster.Follow<(string Id, string Kind), Agent>(now, [("b", "claude"), ("a", "codex")], (x, p) => x.Id == p.Id, (x, _) => x, p => Make(p.Id));
        Assert.Same(b, now[0]);
        Assert.Same(a, now[1]);
        Assert.Empty(gone);
        Assert.Equal(2, made);

        // "a" turned off: only it goes.
        (now, gone) = AgentRoster.Follow<(string Id, string Kind), Agent>(now, [("b", "claude")], (x, p) => x.Id == p.Id, (x, _) => x, p => Make(p.Id));
        Assert.Same(b, Assert.Single(now));
        Assert.Same(a, Assert.Single(gone));
    }

    [Fact]
    public async Task A_conversation_stays_on_its_agent_when_the_list_is_reordered()
    {
        using var first = new LocalAgentRoute("PC", "Codex", new ChatScript(Answer("ok"), Answer("ok"), Answer("ok")), () => null);
        using var second = new LocalAgentRoute("PC", "Claude Code", new ChatScript(Answer("ok"), Answer("ok"), Answer("ok")), () => null);
        var chain = new AgentChain([first, second]);
        Assert.Equal("Codex", (await chain.RunAsync(Job("c1"), TimeSpan.FromSeconds(5), CancellationToken.None)).Agent);

        chain.Use([second, first]);
        first.AgentName = "Codex (work)";

        Assert.Equal("Codex (work)", (await chain.RunAsync(Job("c1"), TimeSpan.FromSeconds(5), CancellationToken.None)).Agent);
        Assert.Equal("Claude Code", (await chain.RunAsync(Job("c2"), TimeSpan.FromSeconds(5), CancellationToken.None)).Agent);
        // Turned off: its conversation goes to the first that can take it.
        chain.Use([second]);
        Assert.Equal("Claude Code", (await chain.RunAsync(Job("c1"), TimeSpan.FromSeconds(5), CancellationToken.None)).Agent);
    }

    // ---- 9. Overlapping waits keep the live channel open ----------------------------------------------------------

    [Fact]
    public void The_live_channel_closes_only_when_the_last_wait_ends()
    {
        var live = false;
        var lease = new LiveLease(() => live, on => live = on, () => { });

        var run = lease.Hold();
        var undo = lease.Hold();
        run.Dispose();
        Assert.True(live);
        run.Dispose();
        Assert.True(live);
        undo.Dispose();
        Assert.False(live);

        // Open before: it stays open.
        live = true;
        lease.Hold().Dispose();
        Assert.True(live);
    }

    [Fact]
    public async Task Many_waits_at_once_leave_the_channel_as_it_was()
    {
        var live = false;
        var lease = new LiveLease(() => Volatile.Read(ref live), on => Volatile.Write(ref live, on), () => { });

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            using var held = lease.Hold();
            Assert.True(Volatile.Read(ref live));
            await Task.Yield();
        })));

        Assert.False(live);
    }

    // ---- 10. One text call for every feature -----------------------------------------------------------------------

    [Fact]
    public async Task A_text_call_that_runs_by_itself_stays_on_the_PC_even_when_it_took_it_and_failed()
    {
        var features = new MemorySettings<AiFeatureSettings>();
        var keys = new AiKeyStore(new MemorySynced<AiKey>());
        keys.Add(AiProviders.Claude, "Helm", "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123-x7Qa");
        var api = new ChatScript(Answer("from the API"));
        var text = new AiTextService(keys, features, new AiUsageBook(new MemorySyncedLog<AiUsageEntry>()), [api], new AiRouter([new Failing()]));
        // Allowed on the API for both, so only who started it differs.
        features.Update(s => s.Features["x.background"] = new AiFeatureChoice { Route = AiRoutePolicy.PreferPc });

        var byItself = await text.AskAsync(new AiTextRequest("x.background", "s", "p") { UserStarted = false }, CancellationToken.None);
        Assert.False(byItself.Ok);
        Assert.Empty(api.Offered);

        var tapped = await text.AskAsync(new AiTextRequest("x.background", "s", "p"), CancellationToken.None);
        Assert.Equal("from the API", tapped.Text);
        // With no choice of its own, what runs by itself is on a PC only.
        Assert.Equal(AiRoutePolicy.PcOnly, text.ChoiceFor("x.other", userStarted: false).Route);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static AiJob Job(string conversation) =>
        new(AiFeatureIds.Default, conversation, "phở 45k", [], [], "claude-haiku-5-5", "low");

    private void Seen(string id, string device, int minutesAgo) =>
        _workers.Upsert(id, new AiWorkerRecord { Device = device, Agents = ["Codex"], SeenAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo) });

    private static string Serialize(object? result) =>
        result as string ?? JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static JsonElement Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static async Task Eventually(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(25);
        Assert.True(done());
    }

    private sealed record Agent(string Id);

    /// <summary>A PC's agent: records each turn and the conversations ended.</summary>
    private sealed class Pc(string device) : IAgentRoute
    {
        public List<AiJob> Jobs { get; } = [];
        public List<string> Ended { get; } = [];
        public string Device => device;
        public string? Unavailable() => null;

        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct)
        {
            lock (Jobs) Jobs.Add(job);
            return Task.FromResult(new AiJobResult($"ok on {device}", [], [], null));
        }

        public void End(string conversationId)
        {
            lock (Ended) Ended.Add(conversationId);
        }
    }

    /// <summary>A PC that takes every job and then fails it.</summary>
    private sealed class Failing : IAgentRoute
    {
        public string Device => "PC-HUNG";
        public string? Unavailable() => null;
        public Task<AiJobResult> RunAsync(AiJob job, TimeSpan wait, CancellationToken ct) => throw new AiRouteTakenException("Codex stopped.");
        public void End(string conversationId) { }
    }

    private sealed class FakeSync : ISyncService
    {
        public SyncStatus Status { get; private set; } = new(SyncState.Idle, null, null);
        public event EventHandler<SyncStatus>? StatusChanged;

        public void Pulled()
        {
            Status = new SyncStatus(SyncState.Idle, DateTimeOffset.UtcNow, null);
            StatusChanged?.Invoke(this, Status);
        }

        public Task<SyncRunResult> SyncNowAsync(CancellationToken ct = default) => Task.FromResult(new SyncRunResult(SyncRunOutcome.Completed, 0, 0, 0));
        public void RequestSync() { }
    }
}
