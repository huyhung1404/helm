using System.Text.Json;
using Helm.Core.Settings;
using Helm.Modules.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>The timeline in pages: step ids from the transcript, the PC's ledger, the phone putting pages back together.</summary>
public sealed class WorkspaceTimelineTests : IAsyncDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("helm-ws-timeline-");
    private readonly MemorySynced<WorkspaceHostRecord> _hosts = new();
    private readonly MemorySynced<WorkspaceSessionRecord> _sessions = new();
    private readonly MemorySynced<WorkspaceCommand> _commands = new();
    private readonly MemorySynced<WorkspaceResultRecord> _results = new();
    private readonly MemorySynced<WorkspaceTimelinePage> _pages = new();
    private readonly string _pipe = "Helm.Workspace.Test." + Guid.NewGuid().ToString("N");
    private readonly SerialUi _ui = new();
    private SettingsStoreFactory? _pcSettings;
    private WorkspaceEngine? _engine;
    private WorkspaceRelay? _relay;
    private FakeBridge? _bridge;

    private static string User(string uuid, string text, int minute = 0) =>
        JsonSerializer.Serialize(new { type = "user", uuid, timestamp = At(minute), message = new { role = "user", content = text } });

    private static string Answer(string uuid, string text, int minute = 0) =>
        JsonSerializer.Serialize(new { type = "assistant", uuid, timestamp = At(minute), message = new { role = "assistant", content = new object[] { new { type = "text", text } } } });

    private static string Call(string uuid, string call, string command) =>
        JsonSerializer.Serialize(new { type = "assistant", uuid, timestamp = At(0), message = new { role = "assistant", content = new object[] { new { type = "tool_use", id = call, name = "Bash", input = new { command } } } } });

    private static string Result(string uuid, string call, string output) =>
        JsonSerializer.Serialize(new { type = "user", uuid, timestamp = At(0), message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = call, content = output } } } });

    private static string At(int minute) => new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero).AddMinutes(minute).ToString("O");

    private string Transcript(IEnumerable<string> lines)
    {
        var path = Path.Combine(_dir.FullName, "s1.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary><paramref name="count"/> prompts, each answered: 2 × count steps.</summary>
    private static IEnumerable<string> Talk(int count, int from = 0) =>
        Enumerable.Range(from, count).SelectMany(i => new[] { User($"u{i}", $"Question {i}", i), Answer($"a{i}", $"Answer {i}", i) });

    private static TranscriptItem Step(string id, string text = "") => new() { Kind = TranscriptItemKind.Answer, Id = id, Text = text.Length > 0 ? text : id };

    [Fact]
    public void Steps_keep_their_ids_from_read_to_read()
    {
        var path = Transcript([User("u1", "Fix it"), Call("c1", "toolu_1", "npm test"), Answer("a1", "Looking."), Answer("a2", "Fixed.")]);
        var first = TranscriptReader.ReadView(path).Items;
        Assert.Equal(["u1", "toolu_1", "a1#0"], first.Select(i => i.Id));
        // The answer's next block joins it and keeps its id; the tool's result fills it in.
        Assert.Equal("Looking.\n\nFixed.", first[2].Text);

        File.AppendAllLines(path, [Result("r1", "toolu_1", "12 passed"), User("u2", "Thanks")]);
        var again = TranscriptReader.ReadView(path).Items;
        Assert.Equal(["u1", "toolu_1", "a1#0", "u2"], again.Select(i => i.Id));
        Assert.Equal("12 passed", again[1].Output);
    }

    [Fact]
    public void The_ledger_keeps_earlier_steps_and_updates_the_ones_that_grew()
    {
        var ledger = new TimelineLedger();
        Assert.True(ledger.Merge([Step("a"), Step("b"), Step("c")]));
        var epoch = ledger.Epoch;
        // The next read sees only the end: b, c grown, d new.
        Assert.True(ledger.Merge([Step("b"), Step("c", "c grown"), Step("d")]));
        Assert.Equal(epoch, ledger.Epoch);
        Assert.Equal(["a", "b", "c", "d"], ledger.Page(0).Select(i => i.Id));
        Assert.Equal("c grown", ledger.Page(0)[2].Text);

        // A read whose start was cut off: its unknown first step is not new, so it is not added at the end.
        Assert.True(ledger.Merge([Step("c-tail"), Step("d"), Step("e")]));
        Assert.Equal(["a", "b", "c", "d", "e"], ledger.Page(0).Select(i => i.Id));

        // Nothing in common: the transcript was replaced. A new writing starts.
        Assert.True(ledger.Merge([Step("x"), Step("y")]));
        Assert.NotEqual(epoch, ledger.Epoch);
        Assert.Equal(["x", "y"], ledger.Page(0).Select(i => i.Id));

        // A step without an id: no pages.
        Assert.False(ledger.Merge([new TranscriptItem { Kind = TranscriptItemKind.Answer, Text = "?" }]));
    }

    [Fact]
    public void The_ledger_keeps_a_steps_page_and_drops_the_oldest_pages()
    {
        var ledger = new TimelineLedger();
        var all = Enumerable.Range(0, 260).Select(i => Step("s" + i)).ToList();
        // Read after read, each seeing the last 60 steps, the way a live session grows.
        for (var end = 60; end <= all.Count; end += 7) Assert.True(ledger.Merge(all.GetRange(end - 60, 60)));
        Assert.True(ledger.Merge(all.GetRange(all.Count - 60, 60)));

        Assert.Equal(WorkspaceSync.MaxTimelineItems, ledger.Count);
        Assert.Equal(3, ledger.FirstPage);
        Assert.Equal(12, ledger.LastPage);
        // Page 5 holds steps 100–119 whatever came after.
        Assert.Equal(Enumerable.Range(100, 20).Select(i => "s" + i), ledger.Page(5).Select(i => i.Id));
        Assert.Empty(ledger.Page(2));

        // Only the page whose step grew is to be written again.
        ledger.TakeChangedPages();
        var read = all.GetRange(all.Count - 60, 60);
        read[^1] = read[^1] with { Text = "grown" };
        Assert.True(ledger.Merge(read));
        Assert.Equal([12], ledger.TakeChangedPages());
        Assert.True(ledger.Merge(read));
        Assert.Empty(ledger.TakeChangedPages());
    }

    [Fact]
    public void A_heavy_page_is_cut_to_fit_a_record()
    {
        var huge = string.Concat(Enumerable.Repeat("Đây là một câu trả lời dài. ", 300));
        var items = Enumerable.Range(0, WorkspaceSync.TimelinePageSize).Select(i => Step("h" + i, huge)).ToList();
        var fit = TimelineLedger.Fit(items);
        Assert.Equal(items.Select(i => i.Id), fit.Select(i => i.Id));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(fit).Length <= WorkspaceSync.MaxPageBytes + 2 * WorkspaceSync.TimelinePageSize);
        Assert.EndsWith("…", fit[0].Text);
        Assert.Equal(TimelineLedger.Fit(items), fit);
    }

    [Fact]
    public void The_phone_puts_the_pages_back_and_fills_in_a_page_not_there_yet()
    {
        var pages = new MemorySynced<WorkspaceTimelinePage>();
        void Page(int p, string epoch, params string[] ids) => pages.Upsert(WorkspaceSync.PageId("s1", p),
            new WorkspaceTimelinePage { MachineId = "pc", SessionId = "s1", Epoch = epoch, Page = p, Items = ids.Select(id => Step(id)).ToList() });
        Page(3, "e1", "a", "b");
        Page(4, "e1", "c", "d");
        Page(2, "old", "zz");
        var record = new WorkspaceSessionRecord
        {
            MachineId = "pc", SessionId = "s1", TimelineEpoch = "e1", TimelineFirstPage = 2, TimelineLastPage = 5,
            // Page 5 has not arrived; the record's newest steps have: d grew, e is new.
            Items = [Step("d", "d grown"), Step("e")],
        };
        var timeline = new SessionTimeline(pages);
        Assert.Equal(["a", "b", "c", "d", "e"], timeline.ItemsOf(record).Select(i => i.Id));
        Assert.Equal("d grown", timeline.ItemsOf(record)[3].Text);

        // A record without pages (an older PC): its own steps.
        Assert.Equal(["d", "e"], timeline.ItemsOf(record with { TimelineEpoch = null }).Select(i => i.Id));
    }

    [Fact]
    public async Task A_long_session_reaches_the_phone_in_pages_and_a_live_one_rewrites_only_its_last()
    {
        var transcript = Transcript(Talk(70));
        var engine = await StartPcAsync();
        engine.Registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.Stop, SessionId = "s1", Cwd = @"c:\work\app", TranscriptPath = transcript, LastAssistantMessage = "Answer 69" });
        var record = await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1") is { TimelineEpoch: not null } r ? r : null);

        // 140 steps in 7 pages; the record itself stays small.
        Assert.Equal(0, record.TimelineFirstPage);
        Assert.Equal(6, record.TimelineLastPage);
        Assert.Equal(7, _pages.All().Count);
        Assert.True(record.Items.Count <= WorkspaceSync.LegacyItems);

        var phone = _ui.Run(() => new WorkspaceRemoteViewModel(_hosts, _sessions, _commands, _results, new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "phone"))), _ui, timeline: _pages));
        Assert.Equal("Answer 69", phone.Timeline[^1].Text);
        // More than one record ever held (60): every step is there to scroll back to.
        Assert.Equal(140 - WorkspaceRemoteViewModel.TimelinePage, phone.HiddenSteps);
        while (phone.HasHiddenSteps) _ui.Run(() => phone.ShowEarlierCommand.Execute(null));
        Assert.Equal("Question 0", phone.Timeline[0].Text);
        Assert.Equal(140, phone.Timeline.Count);

        // The session goes on: only the last page is written again.
        var before = _pages.All().ToDictionary(i => i.Id, i => i.Value);
        File.AppendAllLines(transcript, [User("u70", "One more", 70), Answer("a70", "Done", 70)]);
        engine.Registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.Stop, SessionId = "s1", Cwd = @"c:\work\app", TranscriptPath = transcript, LastAssistantMessage = "Done" });
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1") is { Items: [.., { Text: "Done" }] } r ? r : null);
        var after = _pages.All().ToDictionary(i => i.Id, i => i.Value);
        Assert.Equal(8, after.Count);
        Assert.Equal(before.Keys.Order(), after.Keys.Order().Take(7));
        foreach (var (id, page) in before) Assert.Same(page, after[id]);
        Assert.Equal("Done", phone.Timeline[^1].Text);
    }

    [Fact]
    public async Task A_transcript_without_step_ids_is_sent_whole_as_before()
    {
        var transcript = Transcript(["""{"type":"user","message":{"content":"Hello"}}""", """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi."}]}}"""]);
        var engine = await StartPcAsync();
        engine.Registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.Stop, SessionId = "s1", Cwd = @"c:\work\app", TranscriptPath = transcript, LastAssistantMessage = "Hi." });
        var record = await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1") is { Items.Count: 2 } r ? r : null);
        Assert.Null(record.TimelineEpoch);
        Assert.Empty(_pages.All());
    }

    private async Task<WorkspaceEngine> StartPcAsync()
    {
        _pcSettings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "pc")));
        _engine = WorkspaceEngine.Start(_pcSettings.Get<WorkspaceSettings>(WorkspaceIds.ModuleId), new HelmPaths(_dir.FullName),
            new PromptSender(new NoKeys(), NullLogger<PromptSender>.Instance), NullLogger.Instance, bridgePipe: _pipe, projectsRoot: Path.Combine(_dir.FullName, "no-projects"));
        _bridge = new FakeBridge(_pipe);
        await _bridge.ConnectAsync("app", [@"c:\work\app"]);
        await WorkspaceBridgeTests.WaitAsync(() => _engine.Windows().SingleOrDefault());
        _relay = new WorkspaceRelay(_engine, _hosts, _sessions, _commands, _results, "Desk PC", null, NullLogger.Instance, timeline: _pages);
        return _engine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_relay is not null) await _relay.DisposeAsync();
        if (_bridge is not null) await _bridge.DisposeAsync();
        if (_engine is not null) await _engine.DisposeAsync();
        _pcSettings?.Dispose();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    private sealed class NoKeys : IPromptKeyboard
    {
        public IReadOnlyList<(nint Handle, string Title)> TopWindows() => [];
        public nint Foreground() => 0;
        public bool ForceActivate(nint hwnd) => false;
        public void Activate(nint hwnd) { }
        public bool ModifierDown() => false;
        public void Type(string text) { }
        public void NewLine() { }
        public void Submit(bool ctrl) { }
        public Task Pause(int milliseconds, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> WaitFor(Task task, int milliseconds, CancellationToken ct) => Task.FromResult(task.IsCompleted);
    }
}
