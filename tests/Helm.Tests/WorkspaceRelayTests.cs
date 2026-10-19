using Helm.Core.Settings;
using Helm.Modules.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public class WorkspaceConnectionTests
{
    [Fact]
    public void A_sessions_own_choice_beats_its_windows_which_beats_the_default()
    {
        var s = new WorkspaceSettings();
        string[] windows = [@"c:\work\app", @"c:\work"];
        Assert.True(s.IsConnected("a", @"c:\work\app\src", windows));

        s.ConnectNewSessions = false;
        Assert.False(s.IsConnected("a", @"c:\work\app\src", windows));

        // The deepest window folder decides, not its parent.
        s.WindowLinks[@"c:\work"] = false;
        s.WindowLinks[@"c:\work\app"] = true;
        Assert.True(s.IsConnected("a", @"c:\work\app\src", windows));
        Assert.False(s.IsConnected("b", @"c:\work\other", windows));

        s.SessionLinks["a"] = false;
        Assert.False(s.IsConnected("a", @"c:\work\app\src", windows));
    }
}

/// <summary>The PC relay and the phone's control screen talking through one (in-memory) sync account.</summary>
public sealed class WorkspaceRelayTests : IAsyncDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("helm-ws-relay-");
    private readonly MemorySynced<WorkspaceHostRecord> _hosts = new();
    private readonly MemorySynced<WorkspaceSessionRecord> _sessions = new();
    private readonly MemorySynced<WorkspaceCommand> _commands = new();
    private readonly MemorySynced<WorkspaceResultRecord> _results = new();
    private readonly SettingsStoreFactory _pcSettings;
    private readonly SettingsStoreFactory _phoneSettings;
    private readonly string _pipe = "Helm.Workspace.Test." + Guid.NewGuid().ToString("N");
    private readonly ClaudeKeyboard _keyboard = new();
    // One "UI thread" for the phone: sync events from the PC side never change it beside what the test does.
    private readonly SerialUi _ui = new();
    private WorkspaceEngine? _engine;
    private WorkspaceRelay? _relay;
    private FakeBridge? _bridge;

    public WorkspaceRelayTests()
    {
        _pcSettings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "pc")));
        _phoneSettings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "phone")));
    }

    private async Task<WorkspaceEngine> StartPcAsync()
    {
        var settings = _pcSettings.Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        _engine = WorkspaceEngine.Start(settings, new HelmPaths(_dir.FullName), new PromptSender(_keyboard, NullLogger<PromptSender>.Instance),
            NullLogger.Instance, bridgePipe: _pipe, projectsRoot: Path.Combine(_dir.FullName, "no-projects"));
        _keyboard.Registry = _engine.Registry;
        _bridge = new FakeBridge(_pipe);
        await _bridge.ConnectAsync("app", [@"c:\work\app"]);
        await WorkspaceBridgeTests.WaitAsync(() => _engine.Windows().SingleOrDefault());
        _relay = new WorkspaceRelay(_engine, _hosts, _sessions, _commands, _results, "Desk PC", () => [new SkillInfo("commit", "Commit the changes"), new SkillInfo("compact", "Compact")], NullLogger.Instance);
        return _engine;
    }

    private WorkspaceRemoteViewModel Phone() =>
        _ui.Run(() => new WorkspaceRemoteViewModel(_hosts, _sessions, _commands, _results, _phoneSettings, _ui));

    private static ClaudeHookEvent Hook(string name, string session, string? prompt = null, string? reply = null) => new()
    {
        Event = name, SessionId = session, Cwd = @"c:\work\app", Prompt = prompt, LastAssistantMessage = reply,
    };

    [Fact]
    public async Task The_phone_sees_the_pc_and_its_sessions_and_steers_one()
    {
        var engine = await StartPcAsync();
        engine.Registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, "s1", prompt: "Fix the build"));
        engine.Registry.Apply(Hook(ClaudeHookEvent.Stop, "s1", reply: "Build fixed."));
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1") is { State: SessionState.Done } r ? r : null);

        var phone = Phone();
        var host = Assert.Single(phone.Hosts);
        Assert.True(host.IsOnline);
        Assert.Equal("Desk PC", host.Label);
        Assert.Equal("app", Assert.Single(phone.Projects).Name);
        // Like VS Code, the screen opens on the window's session: it is seen at once.
        var tab = Assert.Single(phone.SessionTabs);
        Assert.True(tab.IsSelected);
        Assert.False(tab.Unseen);
        Assert.Equal(0, phone.Projects[0].Attention);
        Assert.Equal("Build fixed.", phone.Timeline.Last().Text);
        Assert.Equal("Fix the build", phone.Timeline.First(r => r.IsUser).Text);

        _ui.Run(() => phone.Prompt = "/co");
        Assert.Equal(["commit", "compact"], phone.Suggestions.Select(s => s.Name));
        _ui.Run(() => phone.PickSuggestionCommand.Execute(phone.Suggestions[0]));
        Assert.Equal("/commit ", phone.Prompt);

        // A new answer arriving while the conversation is open counts as seen at once.
        engine.Registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, "s1", prompt: "And the docs"));
        engine.Registry.Apply(Hook(ClaudeHookEvent.Stop, "s1", reply: "Docs done."));
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1") is { LastReply: "Docs done." } r ? r : null);
        Assert.Equal(0, phone.Projects[0].Attention);
        Assert.Equal("Docs done.", phone.Timeline.Last().Text);

        _ui.Run(() =>
        {
            phone.Prompt = "Now run the tests";
            phone.SendCommand.Execute(null);
            Assert.True(phone.IsSending);
            // The prompt shows at the end of the timeline while it is on its way, and the card says how far it got.
            Assert.Equal("Now run the tests", phone.Timeline.Last().Text);
            Assert.True(phone.ShowProgress);
            Assert.Equal("Sending your message", phone.ProgressTitle);
            Assert.True(phone.ProgressSteps[0].IsDone);
        });
        await WorkspaceBridgeTests.WaitAsync(() => phone.IsSending ? null : (object)true);
        Assert.False(phone.ShowProgress);
        Assert.Equal("✓ Claude received it", phone.SendStatus);
        Assert.False(phone.SendFailed);
        Assert.Contains("type:Now run the tests", _keyboard.Log);
    }

    [Fact]
    public async Task The_phone_allows_a_held_tool_and_answers_a_question()
    {
        var engine = await StartPcAsync();
        _pcSettings.Get<WorkspaceSettings>(WorkspaceIds.ModuleId).Update(s => s.ApproveFromPhone = PhoneApprovals.Always);
        engine.PhoneLinked = true;
        engine.Registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, "s1", prompt: "Ship it"));
        var hook = engine.HandleHookAsync(PendingRequestTests.Permission("Bash", """{"command":"npm publish"}"""));
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1")?.Pending);

        var phone = _ui.Run(() => new WorkspaceRemoteViewModel(_hosts, _sessions, _commands, _results, _phoneSettings, _ui, dialogs: new Yes()));
        Assert.Equal("s1", phone.OpenSessionId);
        Assert.True(phone.HasPermission);
        Assert.Equal("npm publish", phone.PendingDetail);
        await _ui.Run(() => phone.AllowCommand.ExecuteAsync(null));
        Assert.Contains("\"allow\"", await hook.WaitAsync(TimeSpan.FromSeconds(10)));

        var question = engine.HandleHookAsync(PendingRequestTests.Permission("AskUserQuestion",
            """{"questions":[{"question":"Which?","header":"Pick","multiSelect":false,"options":[{"label":"A","description":""},{"label":"B","description":""}]}]}"""));
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1")?.Pending?.Kind == PendingKind.Question ? (object)true : null);
        await WorkspaceBridgeTests.WaitAsync(() => phone.HasQuestion ? (object)true : null);
        _ui.Run(() => phone.SendAnswersCommand.Execute(null));
        Assert.Contains("Which?", phone.SendStatus);
        _ui.Run(() =>
        {
            phone.Questions[0].Options[0].IsPicked = true;
            phone.Questions[0].Options[1].IsPicked = true;
            Assert.False(phone.Questions[0].Options[0].IsPicked);
            phone.SendAnswersCommand.Execute(null);
        });
        Assert.Contains("\"Which?\":\"B\"", await question.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class Yes : Helm.Shell.Services.IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);
    }

    [Fact]
    public async Task A_new_session_is_started_in_the_window_of_the_tab()
    {
        await StartPcAsync();
        var phone = Phone();
        // A window without a session opens on "New session".
        Assert.True(phone.IsNewSession);
        _ui.Run(() =>
        {
            phone.DraftModel = "haiku";
            phone.Prompt = "Plan the release";
            phone.SendCommand.Execute(null);
        });
        await WorkspaceBridgeTests.WaitAsync(() => phone.IsSending ? null : (object)true);
        Assert.Equal("✓ Claude received it", phone.SendStatus);
        Assert.Equal(["type:/model haiku", "submit", "type:Plan the release", "submit"], _keyboard.Log);
    }

    [Fact]
    public async Task A_disconnected_session_leaves_the_phone_and_cannot_be_steered()
    {
        var engine = await StartPcAsync();
        engine.Registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, "s1", prompt: "Secret work"));
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1"));

        engine.SetSessionLink("s1", false);
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1") is null ? (object)true : null);
        Assert.Empty(Phone().SessionTabs);

        // A command written before the phone noticed is still refused by the PC.
        var result = await engine.RunAsync(new WorkspaceCommand { MachineId = engine.MachineId, SessionId = "s1", Text = "x", CreatedAt = DateTimeOffset.Now }, CancellationToken.None);
        Assert.Equal(WorkspaceCommandStatus.Failed, result.Status);
        Assert.Contains("disconnected", result.Error);

        engine.SetSessionLink("s1", null);
        await WorkspaceBridgeTests.WaitAsync(() => _sessions.Get("s1"));
    }

    [Fact]
    public async Task Commands_for_another_pc_are_left_alone_and_the_pc_says_when_it_stops()
    {
        var engine = await StartPcAsync();
        _commands.SetRemote("other", new WorkspaceCommand { MachineId = "another-pc", SessionId = "x", Text = "Not mine", CreatedAt = DateTimeOffset.Now });
        await Task.Delay(300);
        Assert.Null(_results.Get("other"));

        await _relay!.DisposeAsync();
        _relay = null;
        var host = _hosts.Get(engine.MachineId)!;
        Assert.False(host.Running);
        var phone = Phone();
        Assert.False(phone.HostOnline);
        Assert.Contains("Offline", phone.HostStatus);
        _ui.Run(() => phone.Prompt = "Hello");
        Assert.False(phone.SendCommand.CanExecute(null));
    }

    [Fact]
    public void Records_stay_small()
    {
        var huge = new string('x', 50_000);
        var items = Enumerable.Range(0, 100).Select(i => new TranscriptItem
        {
            Kind = i % 3 == 0 ? TranscriptItemKind.Tool : TranscriptItemKind.Answer,
            Text = huge,
            Tool = "Bash",
            Input = new string('i', TranscriptReader.MaxInput),
            Output = new string('o', TranscriptReader.MaxOutput),
        }).ToList();
        var record = WorkspaceSync.ToRecord(new AgentSession { MachineId = "m", SessionId = "s", Folder = "f", LastReply = huge, LastPrompt = huge },
            "f", new TranscriptView { Items = items, Agents = [new SubagentInfo { Type = "Explore", Task = "Find it" }] });
        Assert.Equal(WorkspaceSync.MaxText, record.LastReply!.Length);
        Assert.InRange(record.Items.Count, 1, WorkspaceSync.MaxItems);
        // The newest steps are the ones kept.
        Assert.Equal(items[^1].Kind, record.Items[^1].Kind);
        Assert.Equal(items[^2].Kind, record.Items[^2].Kind);
        Assert.Empty(record.Agents);
        Assert.True(System.Text.Json.JsonSerializer.Serialize(record).Length < 64 * 1024);
    }

    public async ValueTask DisposeAsync()
    {
        if (_relay is not null) await _relay.DisposeAsync();
        if (_bridge is not null) await _bridge.DisposeAsync();
        if (_engine is not null) await _engine.DisposeAsync();
        _pcSettings.Dispose();
        _phoneSettings.Dispose();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    /// <summary>Types into a pretend VS Code; Enter makes Claude's UserPromptSubmit hook fire with what was typed.</summary>
    private sealed class ClaudeKeyboard : IPromptKeyboard
    {
        private string _typed = "";
        private nint _foreground;

        public SessionRegistry? Registry { get; set; }
        public List<string> Log { get; } = [];
        public string Session { get; set; } = "s1";

        public IReadOnlyList<(nint Handle, string Title)> TopWindows() => [(42, "app - Visual Studio Code")];
        public nint Foreground() => _foreground;
        public bool ForceActivate(nint hwnd) { _foreground = hwnd; return true; }
        public void Activate(nint hwnd) { }
        public bool ModifierDown() => false;
        public void Type(string text) { Log.Add("type:" + text); _typed += text; }
        public void NewLine() => _typed += "\n";

        public void Submit(bool ctrl)
        {
            Log.Add("submit");
            var text = _typed;
            _typed = "";
            if (text.StartsWith('/')) return;
            var session = Registry!.Sessions.Any(s => s.SessionId == Session) ? Session : "new-" + Guid.NewGuid().ToString("N")[..6];
            Registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.UserPromptSubmit, SessionId = session, Cwd = @"c:\work\app", Prompt = text });
        }

        public Task Pause(int milliseconds, CancellationToken ct) => Task.CompletedTask;

        public async Task<bool> WaitFor(Task task, int milliseconds, CancellationToken ct)
        {
            await Task.WhenAny(task, Task.Delay(50, ct));
            return task.IsCompleted;
        }
    }
}

public class ClaudeCatalogTests
{
    [Fact]
    public void Reads_the_commands_of_an_initialize_answer()
    {
        var line = """{"type":"control_response","response":{"subtype":"success","request_id":"helm-init","response":{"commands":[{"name":"commit","description":"Commit the changes","argumentHint":""},{"name":"/review","description":"Review"},{"description":"no name"}],"models":[]}}}""";
        var skills = ClaudeCatalog.ParseInitialize(line)!;
        Assert.Equal([new SkillInfo("commit", "Commit the changes"), new SkillInfo("review", "Review")], skills);
        Assert.Null(ClaudeCatalog.ParseInitialize("""{"type":"system","subtype":"init"}"""));
        Assert.Null(ClaudeCatalog.ParseInitialize("not json"));
    }
}
