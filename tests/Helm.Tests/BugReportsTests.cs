using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Diagnostics;
using Helm.Core.Mcp;
using Helm.Core.Sync;
using Helm.Shell.BugReports;

namespace Helm.Tests;

public sealed class BugReportsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 14, 0, 0, TimeSpan.Zero);

    private sealed class MemoryFiles : IBugFiles
    {
        private readonly Dictionary<string, byte[]> _files = [];
        private int _next;

        public Task<BlobRef> SaveAsync(byte[] data, CancellationToken ct = default)
        {
            var id = $"blob{++_next}";
            _files[id] = data;
            return Task.FromResult(new BlobRef(id, data.Length, 4, 1, [1, 2, 3]));
        }

        public Task<byte[]> ReadAsync(BlobRef blob, CancellationToken ct = default) => Task.FromResult(_files[blob.Id]);
    }

    private sealed class Fixture
    {
        public ManualTime Time { get; } = new(Start);
        public MemorySynced<BugReport> Reports { get; } = new();
        public MemorySynced<BugEntry> Entries { get; } = new();
        public BugStore Store { get; }
        public BlackBox Box { get; }

        public Fixture()
        {
            Store = new BugStore(Reports, Entries, new MemoryFiles(), () => "Home PC", BugDeviceKind.Pc, "0.85.2", Time);
            Box = new BlackBox(Time);
        }

        public BlackBoxSnapshot SomeBox()
        {
            Box.Navigation("Opened Tracker", "Shell");
            Time.Advance(TimeSpan.FromSeconds(2));
            Box.Input("Clicked Save", "TrackerTaskDialog");
            Box.Error("[ERR] Save failed", "TrackerStore", new InvalidOperationException("same key"));
            return Box.Snapshot();
        }

        public Task<string> ReportAsync(string text = "Lưu không được\nChi tiết") =>
            Store.ReportAsync(new BugDraft(text, BugSeverity.Blocking, "Tracker", [new BugImageDraft([9, 9], "image/png", "shot.png")], SomeBox()));
    }

    // ---- black box ----

    [Fact]
    public void The_black_box_keeps_only_the_last_minute()
    {
        var time = new ManualTime(Start);
        var box = new BlackBox(time);
        box.Input("old");
        time.Advance(TimeSpan.FromSeconds(61));
        box.Input("new");
        Assert.Equal(["new"], box.Snapshot().Events.Select(e => e.Text));
    }

    [Fact]
    public void A_frozen_black_box_records_nothing_until_it_thaws()
    {
        var box = new BlackBox(new ManualTime(Start));
        box.Input("before");
        var frozen = box.Freeze();
        box.Input("in the report window");
        Assert.True(box.IsFrozen);
        box.Thaw();
        box.Input("after");
        Assert.Equal(["before"], frozen.Events.Select(e => e.Text));
        Assert.Equal(["before", "after"], box.Snapshot().Events.Select(e => e.Text));
    }

    [Fact]
    public void The_black_box_has_a_cap_and_clips_long_lines()
    {
        var box = new BlackBox(new ManualTime(Start));
        for (var i = 0; i < BlackBox.MaxEvents + 50; i++) box.Input($"e{i}");
        box.Input(new string('x', 5000));
        var snapshot = box.Snapshot();
        Assert.Equal(BlackBox.MaxEvents, snapshot.Events.Count);
        Assert.True(snapshot.Events[^1].Text.Length <= 400);
    }

    [Fact]
    public void A_snapshot_round_trips_and_sums_itself_up()
    {
        var time = new ManualTime(Start);
        var box = new BlackBox(time);
        box.AddContext("Helm", () => "0.85.2");
        box.AddContext("Broken", () => throw new InvalidOperationException());
        box.Error("boom", "X", new InvalidOperationException("bad"));
        UiStallWatch.Record(box, TimeSpan.FromMilliseconds(1800));
        box.Record(BlackBoxKind.DisabledClick, "Clicked Save while it was disabled");
        var back = BlackBoxSnapshot.FromBytes(box.Snapshot().ToBytes());
        Assert.Equal(3, back.Events.Count);
        Assert.Equal(1, back.ErrorCount);
        Assert.Equal(1, back.DisabledClickCount);
        Assert.Equal(TimeSpan.FromMilliseconds(1800), back.LongestFreeze);
        Assert.Contains("InvalidOperationException", back.Events[0].Detail);
        Assert.Equal("0.85.2", back.Context["Helm"]);
        Assert.False(back.Context.ContainsKey("Broken"));
        Assert.Equal("1 error · froze 1.8 s · 1 click on a disabled button", BugText.Summary(back).Replace("3 events · ", ""));
    }

    [Fact]
    public void Helms_log_goes_into_the_black_box_as_the_log_file_writes_it()
    {
        var box = new BlackBox(new ManualTime(Start));
        using var log = new Serilog.LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(new BlackBoxSink(box)).CreateLogger();
        log.ForContext("SourceContext", "Helm.Modules.Tracker.TrackerStore").Information("Module {Id} enabled", "habits");
        log.Debug("not kept");
        log.Error(new InvalidOperationException("same key"), "Saving failed");
        var events = box.Snapshot().Events;
        Assert.Equal(2, events.Count);
        Assert.Equal("[INF] Module habits enabled", events[0].Text);
        Assert.Equal("TrackerStore", events[0].Source);
        Assert.Equal(BlackBoxKind.Error, events[1].Kind);
        Assert.Contains("same key", events[1].Detail);
    }

    [Fact]
    public void A_crash_leaves_its_black_box_for_the_next_start_once()
    {
        var file = Path.Combine(Path.GetTempPath(), $"helm-crash-{Guid.NewGuid():N}.gz");
        var box = new BlackBox(new ManualTime(Start));
        box.Input("Clicked Read aloud");
        box.WriteCrash(file, new NullReferenceException("x"));
        var crash = BlackBox.TakeCrash(file);
        Assert.NotNull(crash);
        Assert.Equal(2, crash.Events.Count);
        Assert.Equal(BlackBoxKind.Error, crash.Events[1].Kind);
        Assert.Null(BlackBox.TakeCrash(file));
    }

    // ---- the store ----

    [Fact]
    public async Task A_report_keeps_the_title_images_and_black_box()
    {
        var f = new Fixture();
        var id = await f.ReportAsync();
        var bug = f.Store.Get(id)!;
        Assert.Equal(1, bug.Number);
        Assert.Equal("B1", bug.Code);
        Assert.Equal("Lưu không được", bug.Title);
        Assert.Equal(BugStatus.Open, bug.Status);
        Assert.Equal("Home PC", bug.Device);
        var entry = Assert.Single(f.Store.Entries(id)).Value;
        Assert.Equal(BugEntryKind.Report, entry.Kind);
        Assert.Single(entry.Images);
        Assert.Equal(3, entry.BlackBoxEvents);
        Assert.Equal(1, entry.BlackBoxErrors);
        var box = await f.Store.ReadBlackBoxAsync(entry);
        Assert.Equal("Clicked Save", box!.Events[1].Text);
        Assert.Equal([9, 9], await f.Store.ReadImageAsync(entry.Images[0]));
        Assert.Equal(id, f.Store.Find("#B1")!.Id);
        Assert.Equal(id, f.Store.Find("1")!.Id);
        Assert.Equal(2, f.Store.Get(await f.ReportAsync("second"))!.Number);
    }

    [Fact]
    public async Task An_empty_report_or_too_many_images_is_refused()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.ReportAsync(new BugDraft("  ", BugSeverity.Minor, "Helm", [], null)));
        var images = Enumerable.Range(0, BugStore.MaxImages + 1).Select(_ => new BugImageDraft([1], "image/png")).ToList();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.ReportAsync(new BugDraft("x", BugSeverity.Minor, "Helm", images, null)));
    }

    [Fact]
    public async Task An_agent_claims_asks_and_resolves_but_only_the_user_closes()
    {
        var f = new Fixture();
        var id = await f.ReportAsync();
        f.Store.Claim(id, "Claude Code · helm", "fix/bug-B1");
        Assert.Equal(BugStatus.Fixing, f.Store.Get(id)!.Status);
        Assert.Throws<InvalidOperationException>(() => f.Store.Claim(id, "Codex", null));

        var ask = f.Store.Ask(id, "Claude Code · helm", "Tiết kiệm pin có bật không?");
        Assert.Equal(ask.EntryId, f.Store.Get(id)!.PendingQuestion);
        await f.Store.AnswerAsync(id, "Có", [], null);
        Assert.Null(f.Store.Get(id)!.PendingQuestion);

        f.Store.Resolve(id, "Claude Code · helm", "Hạn có mili giây", "v0.86.0", null);
        var bug = f.Store.Get(id)!;
        Assert.Equal(BugStatus.ToVerify, bug.Status);
        Assert.Equal("0.86.0", bug.FixedIn);
        Assert.Null(bug.FixingBy);
        Assert.Throws<InvalidOperationException>(() => f.Store.Claim(id, "Claude Code · helm", null));

        Assert.Empty(f.Store.FixedIn("0.85.9"));
        Assert.Single(f.Store.FixedIn("0.86.0"));
        Assert.Single(f.Store.FixedIn("0.86.1-preview.1"));

        f.Store.ConfirmFixed(id);
        Assert.Equal(BugStatus.Closed, f.Store.Get(id)!.Status);
        Assert.Equal(BugCloseReason.Fixed, f.Store.Get(id)!.CloseReason);
        Assert.Throws<InvalidOperationException>(() => f.Store.Claim(id, "Claude Code · helm", null));
    }

    [Fact]
    public async Task A_hand_off_waits_for_the_agent_and_the_user_can_take_it_back()
    {
        var f = new Fixture();
        var id = await f.ReportAsync();
        f.Store.HandOver(id, "Claude Code · helm");
        Assert.Equal(BugStatus.Fixing, f.Store.Get(id)!.Status);
        // The session the user handed it to claims it under its own name.
        f.Store.Claim(id, "Claude Code · helm-bugs", null);
        Assert.Equal("Claude Code · helm-bugs", f.Store.Get(id)!.FixingBy);
        f.Store.TakeBack(id);
        Assert.Equal(BugStatus.Open, f.Store.Get(id)!.Status);
        Assert.Null(f.Store.Get(id)!.FixingBy);
    }

    [Fact]
    public async Task Reopening_needs_a_reason_and_keeps_a_new_black_box()
    {
        var f = new Fixture();
        var id = await f.ReportAsync();
        f.Store.Close(id, BugCloseReason.NotABug, "đúng thiết kế");
        Assert.Equal(BugStatus.Closed, f.Store.Get(id)!.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.ReopenAsync(id, " ", [], null));
        f.Time.Advance(TimeSpan.FromMinutes(5));
        await f.Store.ReopenAsync(id, "vẫn bị", [], f.SomeBox());
        var bug = f.Store.Get(id)!;
        Assert.Equal(BugStatus.Open, bug.Status);
        Assert.Null(bug.CloseReason);
        var latest = f.Store.LatestBlackBox(id)!.Value;
        Assert.Equal(BugEntryKind.Status, latest.Kind);
        Assert.Equal("Reopened: vẫn bị", latest.Text);
    }

    [Fact]
    public async Task Undo_puts_the_bug_back_and_removes_the_entry()
    {
        var f = new Fixture();
        var id = await f.ReportAsync();
        var change = f.Store.Claim(id, "Claude Code", null);
        Assert.True(f.Store.Undo(change));
        Assert.Equal(BugStatus.Open, f.Store.Get(id)!.Status);
        Assert.Single(f.Store.Entries(id));
    }

    [Theory]
    [InlineData("0.85.2", "0.86.0", -1)]
    [InlineData("v0.86.0", "0.86.0", 0)]
    [InlineData("0.86.0-preview.1", "0.86.0", 0)]
    [InlineData("1.0.0", "0.99.9", 1)]
    public void Versions_compare_by_number(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(BugStore.CompareVersions(a, b)));

    // ---- MCP ----

    private static async Task<JsonNode> Call(BugMcpTools tools, string name, object args)
    {
        var tool = tools.Tools.Single(t => t.Name == name);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        var (result, ok) = await McpServer.RunToolAsync(tool, doc.RootElement, null, default);
        Assert.True(ok, result.ToJsonString());
        return result;
    }

    private static JsonNode Text(JsonNode result) => JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;

    [Fact]
    public async Task Agents_read_a_bug_with_its_black_box_summed_up()
    {
        var f = new Fixture();
        await f.ReportAsync();
        var tools = new BugMcpTools(f.Store);

        var list = Text(await Call(tools, "bugs_list", new { }));
        Assert.Equal("B1", list["bugs"]![0]!["bug"]!.GetValue<string>());

        var bug = Text(await Call(tools, "bug_get", new { bug = "B1" }));
        Assert.Equal("open", bug["status"]!.GetValue<string>());
        var box = bug["black_box"]!;
        Assert.Contains("Clicked Save", box["steps"]![1]!.GetValue<string>());
        Assert.Contains("same key", box["errors"]![0]!["stack"]!.GetValue<string>());

        var events = Text(await Call(tools, "bug_blackbox", new { bug = "1", kinds = new[] { "error" } }));
        Assert.Single(events["events"]!.AsArray());
    }

    [Fact]
    public async Task Agents_get_the_pictures_as_images_and_files()
    {
        var f = new Fixture();
        await f.ReportAsync();
        var folder = Path.Combine(Path.GetTempPath(), $"helm-bug-tests-{Guid.NewGuid():N}");
        var result = await Call(new BugMcpTools(f.Store, () => folder), "bug_images", new { bug = "B1" });
        var content = result["content"]!.AsArray();
        Assert.Equal("image", content[1]!["type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([9, 9]), content[1]!["data"]!.GetValue<string>());
        var file = Text(result)["images"]![0]!["file"]!.GetValue<string>();
        Assert.Equal([9, 9], File.ReadAllBytes(file));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task An_agent_resolves_and_its_change_can_be_undone()
    {
        var f = new Fixture();
        var id = await f.ReportAsync();
        var tools = new BugMcpTools(f.Store);
        await Call(tools, "bug_claim", new { bug = "B1", project = "helm" });
        Assert.Equal("AI agent · helm", f.Store.Get(id)!.FixingBy);
        var resolved = await Call(tools, "bug_resolve", new { bug = "B1", resolution = "fixed it", fixed_in = "0.86.0" });
        Assert.Equal("to_verify", Text(resolved)["status"]!.GetValue<string>());

        var tool = tools.Tools.Single(t => t.Name == "bug_resolve");
        using var doc = JsonDocument.Parse(resolved["content"]![0]!["text"]!.GetValue<string>());
        await tool.Undo!(doc.RootElement, default);
        Assert.Equal(BugStatus.Fixing, f.Store.Get(id)!.Status);
    }

    [Fact]
    public void Bug_tools_are_always_offered_and_only_reads_are_read_only()
    {
        var tools = new BugMcpTools(new Fixture().Store);
        Assert.Null(tools.ModuleId);
        Assert.Equal(["bug_blackbox", "bug_get", "bug_images", "bugs_list"],
            tools.Tools.Where(t => t.ReadOnly).Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools.Tools.Where(t => !t.ReadOnly), t => Assert.NotNull(t.Undo));
    }
}
