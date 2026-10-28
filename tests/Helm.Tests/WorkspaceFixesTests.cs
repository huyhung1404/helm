using System.IO.Pipes;
using System.Text;
using Helm.Core.Settings;
using Helm.Modules.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>Where a session belongs, whatever folder its shell went to.</summary>
public sealed class WorkspaceHomeFolderTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("helm-ws-home-");

    [Theory]
    [InlineData("/c/Project/Helm", @"C:\Project\Helm")]
    [InlineData("/c", @"C:\")]
    [InlineData(@"C:\x\y", @"C:\x\y")]
    [InlineData(@"\\server\share", @"\\server\share")]
    public void Git_bash_paths_read_as_windows_paths(string path, string native) => Assert.Equal(native, WorkspaceFolders.Native(path));

    [Fact]
    public void A_git_bash_path_is_inside_its_windows_folder()
    {
        Assert.True(WorkspaceFolders.IsUnder("/c/work/app/src", @"C:\work\app"));
        Assert.Equal(SessionRegistry.FolderKey(@"C:\work\app"), SessionRegistry.FolderKey("/c/work/app/"));
    }

    [Fact]
    public void A_session_keeps_the_folder_claude_filed_it_under()
    {
        var transcript = Transcript("c--work-app", "s1", @"c:\work\app", @"C:\tmp\worktree");
        Assert.Equal(@"c:\work\app", ClaudeProjects.HomeOf(transcript, @"C:\tmp\worktree"));
        Assert.Equal(@"c:\work\app", ClaudeProjects.HomeOf(transcript, @"c:\work\app\src"));
        Assert.Equal(@"C:\work\app", ClaudeProjects.HomeOf(transcript, "/c/work/app"));
        Assert.Null(ClaudeProjects.HomeOf("", @"c:\work\app"));
    }

    internal string Transcript(string key, string id, params string[] cwds)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir.FullName, "projects", key));
        var path = Path.Combine(dir.FullName, id + ".jsonl");
        File.WriteAllLines(path, cwds.Select(c => $$"""{"type":"user","cwd":{{System.Text.Json.JsonSerializer.Serialize(c)}},"sessionId":"{{id}}"}"""));
        return path;
    }

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { }
    }
}

public sealed class WorkspaceTabMatchTests
{
    [Fact]
    public void A_tab_whose_name_fits_nothing_goes_to_the_session_helm_just_opened()
    {
        var candidates = new List<ClaudeTabMatch.Candidate>
        {
            new("a", "Fix the build", null, DateTimeOffset.Now),
            new("b", "Write the docs", null, DateTimeOffset.Now.AddMinutes(-5)),
        };
        var (byName, _) = ClaudeTabMatch.Match([("Fix the build", false), ("Renamed in VS Code", true)], candidates);
        Assert.Equal(["a", null], byName);

        var (sessions, active) = ClaudeTabMatch.Match([("Fix the build", false), ("Renamed in VS Code", true)], candidates, ["b"]);
        Assert.Equal(["a", "b"], sessions);
        Assert.Equal("b", active);
    }

    [Fact]
    public void An_ended_session_opened_again_is_listed_as_finished()
    {
        var registry = new SessionRegistry("pc");
        registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.UserPromptSubmit, SessionId = "s1", Cwd = @"c:\work", Prompt = "Hi" });
        registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.Stop, SessionId = "s1", Cwd = @"c:\work", LastAssistantMessage = "Hello" });
        registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.SessionEnd, SessionId = "s1", Cwd = @"c:\work" });
        Assert.Equal(SessionState.Ended, registry.Find("s1")!.State);

        registry.Revive("s1");
        Assert.Equal(SessionState.Done, registry.Find("s1")!.State);
    }
}

/// <summary>The engine with a VS Code window, sessions whose shell wandered, tabs, and sessions outside VS Code.</summary>
public sealed class WorkspaceEngineFixesTests : IAsyncDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("helm-ws-fix-");
    private readonly string _pipe = "Helm.Workspace.Test." + Guid.NewGuid().ToString("N");
    private readonly FakeClaude _claude = new();
    private WorkspaceEngine? _engine;
    private FakeBridge? _bridge;

    private string Projects => Path.Combine(_dir.FullName, "projects");

    private WorkspaceEngine Start()
    {
        var settings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "pc"))).Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        _engine = WorkspaceEngine.Start(settings, new HelmPaths(_dir.FullName), new PromptSender(new NoKeys(), NullLogger<PromptSender>.Instance),
            NullLogger.Instance, bridgePipe: _pipe, projectsRoot: Projects, headless: new HeadlessClaude(_claude, NullLogger.Instance));
        return _engine;
    }

    private async Task ConnectAsync(string folder)
    {
        _bridge = new FakeBridge(_pipe);
        await _bridge.ConnectAsync("app", [folder]);
        await WorkspaceBridgeTests.WaitAsync(() => _engine!.Windows().SingleOrDefault());
    }

    private string Transcript(string folder, string id, params string[] cwds)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Projects, ClaudeProjects.ProjectKey(folder)));
        var path = Path.Combine(dir.FullName, id + ".jsonl");
        File.WriteAllLines(path, cwds.Select(c => $$"""{"type":"user","cwd":{{System.Text.Json.JsonSerializer.Serialize(c)}},"sessionId":"{{id}}"}"""));
        return path;
    }

    private static ClaudeHookEvent Hook(string name, string id, string cwd, string transcript, string? prompt = null, string? reply = null) => new()
    {
        Event = name, SessionId = id, Cwd = cwd, TranscriptPath = transcript, Prompt = prompt, LastAssistantMessage = reply,
    };

    [Fact]
    public async Task A_session_whose_shell_moved_to_a_worktree_stays_in_its_vs_code_window()
    {
        var engine = Start();
        var app = Directory.CreateDirectory(Path.Combine(_dir.FullName, "app")).FullName;
        var transcript = Transcript(app, "s1", app, @"C:\tmp\worktree");
        // Before the window connects: the folder Claude filed it under, not where the shell is.
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.UserPromptSubmit, "s1", @"C:\tmp\worktree", transcript, prompt: "Go"));
        Assert.Equal(app, engine.Registry.Find("s1")!.Folder, ignoreCase: true);

        await ConnectAsync(app);
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.PostToolUse, "s1", "/c/somewhere/else", transcript));
        Assert.Contains(engine.Windows().Single().Sessions, s => s.SessionId == "s1");
        Assert.Empty(engine.OtherSessions());
    }

    [Fact]
    public async Task A_tab_still_open_after_vs_code_reloaded_is_listed_again()
    {
        var engine = Start();
        var app = Directory.CreateDirectory(Path.Combine(_dir.FullName, "app")).FullName;
        var transcript = Transcript(app, "s1", app);
        await ConnectAsync(app);
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.UserPromptSubmit, "s1", app, transcript, prompt: "Go"));
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.Stop, "s1", app, transcript, reply: "Done"));
        engine.Registry.SetTitle("s1", "Fix the build");
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.SessionEnd, "s1", app, transcript));

        await _bridge!.TabsAsync("app", [app], ("Fix the build", true));
        await WorkspaceBridgeTests.WaitAsync(() => engine.Registry.Find("s1") is { State: SessionState.Done } s ? s : null);
        Assert.Equal(["s1"], engine.Windows().Single().OpenSessions);
    }

    [Fact]
    public async Task A_session_outside_vs_code_goes_on_in_the_background_for_the_phone()
    {
        var engine = Start();
        var folder = Directory.CreateDirectory(Path.Combine(_dir.FullName, "cli")).FullName;
        var transcript = Transcript(folder, "s1", folder);
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.UserPromptSubmit, "s1", folder, transcript, prompt: "Hi"));
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.Stop, "s1", folder, transcript, reply: "Hello"));
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.SessionEnd, "s1", folder, transcript));

        var steps = new List<string>();
        var run = engine.RunAsync(new WorkspaceCommand { MachineId = engine.MachineId, Kind = WorkspaceCommandKind.Prompt, SessionId = "s1", Text = "Run the tests" },
            CancellationToken.None, s => { lock (steps) steps.Add(s); });
        var process = await WorkspaceBridgeTests.WaitAsync(() => _claude.Last);
        Assert.Equal(folder, _claude.Folder, ignoreCase: true);
        Assert.Equal(["-p", "--resume", "s1"], _claude.Args!.Take(3));
        Assert.Contains("stdio", _claude.Args!);
        Assert.Contains("\"content\":\"Run the tests\"", await process.ReadInputAsync());

        // Claude's hook says it has the prompt.
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.UserPromptSubmit, "s1", folder, transcript, prompt: "Run the tests"));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkspaceCommandStatus.Received, result.Status);
        Assert.True(engine.Registry.Find("s1")!.Hosted);
        Assert.Contains("Starting Claude in the background on the PC", steps);

        // A permission it asks waits for the phone, whatever the approval setting says.
        var hook = engine.HandleHookAsync(PendingRequestTests.Permission("Bash", """{"command":"npm test"}""") with { TranscriptPath = transcript, Cwd = folder });
        var pending = await WorkspaceBridgeTests.WaitAsync(() => engine.Registry.Find("s1")?.Pending);
        var answer = await engine.RunAsync(new WorkspaceCommand { MachineId = engine.MachineId, Kind = WorkspaceCommandKind.Answer, SessionId = "s1", RequestId = pending.RequestId, Allow = true },
            CancellationToken.None);
        Assert.Equal(WorkspaceCommandStatus.Received, answer.Status);
        Assert.Contains("\"allow\"", await hook.WaitAsync(TimeSpan.FromSeconds(10)));

        // A permission nobody decided reaches Helm as Claude's own question: refused.
        process.Write("""{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{}}}""");
        var reply = await process.ReadInputAsync();
        Assert.Contains("\"deny\"", reply);
        Assert.Contains("r1", reply);

        // The last answer: its input closes, and the run is over.
        process.Write("""{"type":"result","subtype":"success","queued_turn_count":0}""");
        Assert.Null(await process.ReadInputAsync());
        process.Exit();
        await WorkspaceBridgeTests.WaitAsync(() => engine.Registry.Find("s1") is { Hosted: false } s ? s : null);
    }

    [Fact]
    public async Task A_session_still_working_in_its_terminal_is_not_taken_over()
    {
        var engine = Start();
        var folder = Directory.CreateDirectory(Path.Combine(_dir.FullName, "cli")).FullName;
        var transcript = Transcript(folder, "s1", folder);
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.UserPromptSubmit, "s1", folder, transcript, prompt: "Long job"));

        var result = await engine.RunAsync(new WorkspaceCommand { MachineId = engine.MachineId, Kind = WorkspaceCommandKind.Prompt, SessionId = "s1", Text = "More" },
            CancellationToken.None);
        Assert.Equal(WorkspaceCommandStatus.Failed, result.Status);
        Assert.Contains("still working", result.Error);
        Assert.Null(_claude.Last);
    }

    [Fact]
    public async Task The_phone_sets_the_model_of_a_session_helm_runs_for_its_next_run()
    {
        var engine = Start();
        var folder = Directory.CreateDirectory(Path.Combine(_dir.FullName, "cli")).FullName;
        var transcript = Transcript(folder, "s1", folder);
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.Stop, "s1", folder, transcript, reply: "Hello"));

        var set = await engine.RunAsync(new WorkspaceCommand { MachineId = engine.MachineId, Kind = WorkspaceCommandKind.SetModel, SessionId = "s1", Text = "haiku" }, CancellationToken.None);
        Assert.Equal(WorkspaceCommandStatus.Received, set.Status);
        var run = engine.RunAsync(new WorkspaceCommand { MachineId = engine.MachineId, Kind = WorkspaceCommandKind.Prompt, SessionId = "s1", Text = "Hi" }, CancellationToken.None);
        await WorkspaceBridgeTests.WaitAsync(() => engine.HostedRuns.SingleOrDefault());
        Assert.Contains("\"content\":\"Hi\"", await _claude.Last!.ReadInputAsync());
        Assert.Equal("haiku", _claude.Args![_claude.Args.ToList().IndexOf("--model") + 1]);
        var stop = await engine.RunAsync(new WorkspaceCommand { MachineId = engine.MachineId, Kind = WorkspaceCommandKind.Stop, SessionId = "s1" }, CancellationToken.None);
        Assert.Equal(WorkspaceCommandStatus.Received, stop.Status);
        Assert.True(_claude.Last!.Killed);
        await WorkspaceBridgeTests.WaitAsync(() => engine.HostedRuns.Count == 0 ? (object)true : null);
        await engine.HandleHookAsync(Hook(ClaudeHookEvent.UserPromptSubmit, "s1", folder, transcript, prompt: "Hi"));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_run_stopped_while_a_message_goes_in_is_not_started_again()
    {
        var claude = new FakeClaude();
        var headless = new HeadlessClaude(claude, NullLogger.Instance);
        var folder = Directory.CreateDirectory(Path.Combine(_dir.FullName, "cli")).FullName;
        await headless.SendAsync("s1", folder, "One", CancellationToken.None);
        var first = claude.Last!;
        Assert.True(headless.Stop("s1"));
        first.Dispose();
        var second = await Record.ExceptionAsync(() => headless.SendAsync("s1", folder, "Two", CancellationToken.None));
        // Either the stopped run is gone already (a new run takes "Two") or the write fails and says it was stopped: never a crash.
        Assert.True(second is null or PromptSendException, second?.ToString());
        await headless.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null) await _bridge.DisposeAsync();
        if (_engine is not null) await _engine.DisposeAsync();
        try { _dir.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>No VS Code window takes keys in these tests.</summary>
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

    /// <summary>Plays Claude Code: what Helm writes to its input can be read, and its output written.</summary>
    internal sealed class FakeClaude : IClaudeStarter
    {
        public FakeProcess? Last { get; private set; }
        public string? Folder { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }

        public IClaudeProcess? Start(string folder, IReadOnlyList<string> args)
        {
            Folder = folder;
            Args = args;
            return Last = new FakeProcess();
        }
    }

    internal sealed class FakeProcess : IClaudeProcess
    {
        private readonly AnonymousPipeServerStream _inServer = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream _inClient;
        private readonly AnonymousPipeServerStream _outServer = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _outClient;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeProcess()
        {
            _inClient = new AnonymousPipeClientStream(PipeDirection.Out, _inServer.ClientSafePipeHandle);
            _outClient = new AnonymousPipeClientStream(PipeDirection.In, _outServer.ClientSafePipeHandle);
            _reader = new StreamReader(_inServer, Encoding.UTF8);
            _writer = new StreamWriter(_outServer, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public Stream Input => _inClient;
        public Stream Output => _outClient;
        public Task Exited => _exited.Task;
        public bool Killed { get; private set; }

        public Task<string?> ReadInputAsync() => _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));

        public void Write(string line) => _writer.WriteLine(line);

        public void Exit()
        {
            _writer.Dispose();
            _exited.TrySetResult();
        }

        public void Kill()
        {
            Killed = true;
            try { _writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            _exited.TrySetResult();
        }

        public void Dispose()
        {
            _inClient.Dispose();
            _outClient.Dispose();
        }
    }
}

/// <summary>The phone's screen: sessions outside VS Code, the progress card, how fresh the data is.</summary>
public sealed class WorkspacePhoneFixesTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("helm-ws-phone-");
    private readonly MemorySynced<WorkspaceHostRecord> _hosts = new();
    private readonly MemorySynced<WorkspaceSessionRecord> _sessions = new();
    private readonly MemorySynced<WorkspaceCommand> _commands = new();
    private readonly MemorySynced<WorkspaceResultRecord> _results = new();
    private readonly SerialUi _ui = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));

    private WorkspaceRemoteViewModel Phone(bool agree = true) => _ui.Run(() => new WorkspaceRemoteViewModel(_hosts, _sessions, _commands, _results,
        new SettingsStoreFactory(new HelmPaths(_dir.FullName)), _ui, time: _time, dialogs: new Answer(agree)));

    private void Pc(params HostWindowInfo[] windows) => _hosts.Upsert("pc", new WorkspaceHostRecord
    {
        MachineId = "pc", MachineName = "Desk", LastSeen = _time.GetLocalNow(), Running = true, Windows = windows,
    });

    private void Session(string id, string windowFolder, SessionState state = SessionState.Done, bool hosted = false) => _sessions.Upsert(id, new WorkspaceSessionRecord
    {
        MachineId = "pc", SessionId = id, WindowFolder = windowFolder, Folder = windowFolder.Length > 0 ? windowFolder : @"c:\cli", Title = id, State = state,
        UpdatedAt = _time.GetLocalNow(), LastReply = "ok", Hosted = hosted,
    });

    [Fact]
    public async Task Sessions_outside_vs_code_have_their_own_tab_and_can_be_continued()
    {
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app", OpenSessions = ["a"] });
        Session("a", @"c:\app");
        Session("term", "");
        var phone = Phone();
        Assert.Equal(["app", "Outside VS Code"], phone.Projects.Select(p => p.Name));

        _ui.Run(() => phone.SelectProjectCommand.Execute(phone.Projects[1]));
        Assert.True(phone.IsOutsideProject);
        Assert.False(phone.CanStartNew);
        Assert.Equal("term", phone.OpenSessionId);
        _ui.Run(() => phone.NewSessionCommand.Execute(null));
        Assert.False(phone.IsNewSession);

        _ui.Run(() => phone.Prompt = "Carry on");
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        var command = Assert.Single(_commands.All()).Value;
        Assert.Equal(WorkspaceCommandKind.Prompt, command.Kind);
        Assert.Equal("term", command.SessionId);
        Assert.Equal("Sending it to Claude on the PC", phone.ProgressTitle);
    }

    [Fact]
    public async Task Saying_no_to_taking_a_session_over_sends_nothing()
    {
        Pc();
        Session("term", "");
        var phone = Phone(agree: false);
        _ui.Run(() => phone.Prompt = "Carry on");
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        Assert.Empty(_commands.All());
        Assert.Equal("Carry on", phone.Prompt);
    }

    [Fact]
    public async Task A_new_session_shows_each_step_until_its_tab_opens_here()
    {
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app" });
        var phone = Phone();
        Assert.True(phone.IsNewSession);
        _ui.Run(() => phone.Prompt = "Build a game");
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        Assert.True(phone.ShowProgress);
        Assert.Equal("Starting a new session in app", phone.ProgressTitle);
        Assert.Equal(["Sent from this phone", "Waiting for Desk…"], phone.ProgressSteps.Select(s => s.Text));

        var id = Assert.Single(_commands.All()).Id;
        Result(id, WorkspaceCommandStatus.Sending, step: "Opening a new Claude tab in app");
        Assert.Equal(["Sent from this phone", "Desk picked it up", "Opening a new Claude tab in app"], phone.ProgressSteps.Select(s => s.Text));
        Assert.True(phone.ProgressSteps[^1].IsActive);

        Result(id, WorkspaceCommandStatus.Received, session: "new");
        Assert.True(phone.ShowProgress);
        Assert.Equal("Opening it here…", phone.ProgressSteps[^1].Text);

        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app", OpenSessions = ["new"] });
        Session("new", @"c:\app", SessionState.Working);
        Assert.False(phone.ShowProgress);
        Assert.Equal("new", phone.OpenSessionId);
    }

    [Fact]
    public void The_chip_says_whether_what_you_see_is_fresh()
    {
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app" });
        var phone = Phone();
        Assert.True(phone.FreshLive);
        Assert.Equal("Live", phone.FreshnessText);

        _time.Advance(TimeSpan.FromSeconds(100));
        _ui.Run(() => phone.RefreshNowCommand.Execute(null));
        Assert.True(phone.FreshDelayed);
        Assert.Equal("1 min ago", phone.FreshnessText);
        var ping = Assert.Single(_commands.All()).Value;
        Assert.Equal(WorkspaceCommandKind.Ping, ping.Kind);

        // The PC answers the check: fresh again.
        Result(ping.Id, WorkspaceCommandStatus.Received);
        Assert.True(phone.FreshLive);

        _time.Advance(TimeSpan.FromMinutes(5));
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app" });
        _hosts.Upsert("pc", _hosts.Get("pc")! with { Activities = ["Running Claude in the background: term"] });
        _ui.Run(() => phone.OpenSheetCommand.Execute("Status"));
        Assert.Equal(["Running Claude in the background: term"], phone.PcActivities);
    }

    [Fact]
    public async Task A_message_on_its_way_stays_with_its_session_and_holds_up_no_other()
    {
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app", OpenSessions = ["a", "b"] });
        Session("a", @"c:\app");
        Session("b", @"c:\app");
        var phone = Phone();
        Assert.Equal("a", phone.OpenSessionId);
        _ui.Run(() => phone.Prompt = "Fix the build");
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        var first = Assert.Single(_commands.All()).Id;
        Assert.True(phone.ShowProgress);
        Assert.True(phone.IsSending);

        // Another tab: no card over its chat, its composer is free, the message does not show in its timeline.
        _ui.Run(() => phone.SelectSessionCommand.Execute(phone.SessionTabs[1]));
        Assert.Equal("b", phone.OpenSessionId);
        Assert.False(phone.ShowProgress);
        Assert.False(phone.IsSending);
        Assert.Null(phone.SendStatus);
        Assert.DoesNotContain(phone.Timeline, r => r.Text == "Fix the build");
        _ui.Run(() => phone.Prompt = "Write the docs");
        Assert.True(phone.SendCommand.CanExecute(null));
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        Assert.Equal(2, _commands.All().Count());
        Assert.True(phone.ShowProgress);
        Assert.Equal("Write the docs", phone.Timeline.Last(r => r.IsUser).Text);

        // Back in the first tab: its own card, still on its way.
        _ui.Run(() => phone.SelectSessionCommand.Execute(phone.SessionTabs[0]));
        Assert.True(phone.ShowProgress);
        Assert.True(phone.IsSending);
        Assert.Equal("Fix the build", phone.Timeline.Last(r => r.IsUser).Text);

        // Claude has the second one while you look at the first: nothing changes here.
        var second = _commands.All().Single(c => c.Id != first).Id;
        Result(second, WorkspaceCommandStatus.Received);
        Assert.True(phone.ShowProgress);
        Result(first, WorkspaceCommandStatus.Received);
        Assert.False(phone.ShowProgress);
        Assert.False(phone.IsSending);
        Assert.Equal("✓ Claude received it", phone.SendStatus);

        _ui.Run(() => phone.SelectSessionCommand.Execute(phone.SessionTabs[1]));
        Assert.False(phone.ShowProgress);
        Assert.False(phone.IsSending);
    }

    [Fact]
    public async Task The_steps_of_a_message_on_its_way_are_on_the_timeline_with_their_times()
    {
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app", OpenSessions = ["a"] });
        Session("a", @"c:\app");
        var phone = Phone();
        _ui.Run(() => phone.Prompt = "Fix the build");
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        var id = Assert.Single(_commands.All()).Id;
        Assert.Equal(["Fix the build", "Sent from this phone", "Waiting for Desk…"], phone.Timeline.TakeLast(3).Select(r => r.Text));
        Assert.True(phone.Timeline[^1].StepActive);

        _time.Advance(TimeSpan.FromSeconds(1.5));
        Result(id, WorkspaceCommandStatus.Sending, step: "Typing it into VS Code");
        var steps = phone.Timeline.Where(r => r.IsStep).ToList();
        Assert.Equal(["Sent from this phone", "Desk picked it up", "Typing it into VS Code"], steps.Select(r => r.Text));
        Assert.Equal("1.5 s", steps[1].Took);
        Assert.True(steps[2].StepActive);

        _time.Advance(TimeSpan.FromSeconds(2));
        Result(id, WorkspaceCommandStatus.Received);
        Assert.Equal("Claude received it", phone.Timeline[^1].Text);
        Assert.Equal("in 3.5 s", phone.Timeline[^1].Took);
        Assert.False(phone.ShowStatus);

        // The transcript has it: one quiet line under it says how long it took, Claude's steps go on below.
        _sessions.Upsert("a", _sessions.Get("a")! with
        {
            Items = [new TranscriptItem { Kind = TranscriptItemKind.User, Text = "Fix the build", At = _time.GetLocalNow() }],
            State = SessionState.Working,
        });
        var mine = phone.Timeline.ToList().FindIndex(r => r.IsUser && r.Text == "Fix the build");
        Assert.Equal("Claude received it", phone.Timeline[mine + 1].Text);
        Assert.Single(phone.Timeline, r => r.IsStep);
        Assert.True(phone.Timeline[^1].IsWorking);
    }

    [Fact]
    public async Task A_new_sessions_first_prompt_shows_its_steps_in_place_of_the_page()
    {
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app" });
        var phone = Phone();
        Assert.True(phone.ShowNewIntro);
        Assert.False(phone.ShowTimeline);
        _ui.Run(() => phone.Prompt = "Build a game");
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        Assert.False(phone.ShowNewIntro);
        Assert.True(phone.ShowTimeline);
        Assert.Equal(["Build a game", "Sent from this phone", "Waiting for Desk…"], phone.Timeline.Select(r => r.Text));
    }

    [Fact]
    public async Task A_message_that_failed_while_you_were_in_another_tab_waits_for_you_there()
    {
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app", OpenSessions = ["a", "b"] });
        Session("a", @"c:\app");
        Session("b", @"c:\app");
        var phone = Phone();
        _ui.Run(() => phone.Prompt = "Fix the build");
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        var id = Assert.Single(_commands.All()).Id;

        _ui.Run(() => phone.SelectSessionCommand.Execute(phone.SessionTabs[1]));
        Result(id, WorkspaceCommandStatus.Failed);
        Assert.False(phone.ShowProgress);
        Assert.False(phone.SendFailed);

        _ui.Run(() => phone.SelectSessionCommand.Execute(phone.SessionTabs[0]));
        Assert.True(phone.ShowProgress);
        Assert.True(phone.ProgressFailed);
        Assert.True(phone.SendFailed);

        // Seen: leaving the tab puts it away.
        _ui.Run(() => phone.SelectSessionCommand.Execute(phone.SessionTabs[1]));
        _ui.Run(() => phone.SelectSessionCommand.Execute(phone.SessionTabs[0]));
        Assert.False(phone.ShowProgress);
        Assert.False(phone.SendFailed);
    }

    [Fact]
    public async Task A_new_session_opened_while_you_were_in_another_tab_does_not_pull_you_over()
    {
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app", OpenSessions = ["a"] });
        Session("a", @"c:\app");
        var phone = Phone();
        _ui.Run(() => phone.NewSessionCommand.Execute(null));
        _ui.Run(() => phone.Prompt = "Build a game");
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        var id = Assert.Single(_commands.All()).Id;
        Assert.True(phone.ShowProgress);

        _ui.Run(() => phone.SelectSessionCommand.Execute(phone.SessionTabs[0]));
        Assert.False(phone.ShowProgress);
        Result(id, WorkspaceCommandStatus.Received, session: "new");
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app", OpenSessions = ["a", "new"] });
        Session("new", @"c:\app", SessionState.Working);
        Assert.Equal("a", phone.OpenSessionId);
        Assert.False(phone.ShowProgress);
        Assert.Equal(["a", "new"], phone.SessionTabs.Select(t => t.SessionId));
    }

    [Fact]
    public async Task A_message_whose_pc_went_quiet_is_given_up_and_comes_back_if_the_pc_answers()
    {
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app", OpenSessions = ["a"] });
        Session("a", @"c:\app");
        var phone = Phone();
        _ui.Run(() => phone.Prompt = "Fix the build");
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        var id = Assert.Single(_commands.All()).Id;
        Result(id, WorkspaceCommandStatus.Sending, step: "Typing it into VS Code", run: "r1");

        // The PC renews its lease: on its way, however long it takes.
        for (var i = 0; i < 6; i++)
        {
            _time.Advance(WorkspaceResultRecord.RenewEvery);
            Result(id, WorkspaceCommandStatus.Sending, step: "Typing it into VS Code", run: "r1");
            Tick(phone);
        }
        Assert.True(phone.IsSending);
        Assert.False(phone.SendFailed);

        // Then it goes quiet: given up, said why, the composer is free again.
        _time.Advance(WorkspaceResultRecord.LostAfter + TimeSpan.FromSeconds(1));
        Tick(phone);
        Assert.False(phone.IsSending);
        Assert.True(phone.SendFailed);
        Assert.Equal(DeliveryProgress.LostText, phone.SendStatus);
        Assert.True(phone.ProgressFailed);
        Assert.Equal(DeliveryProgress.LostText, phone.ProgressSteps[^1].Text);

        // It answers after all: the card says so.
        Result(id, WorkspaceCommandStatus.Received, run: "r1");
        Assert.False(phone.SendFailed);
        Assert.Equal("✓ Claude received it", phone.SendStatus);
    }

    [Fact]
    public async Task A_pc_from_before_the_lease_is_never_given_up_on()
    {
        Pc(new HostWindowInfo { Folder = @"c:\app", Name = "app", OpenSessions = ["a"] });
        Session("a", @"c:\app");
        var phone = Phone();
        _ui.Run(() => phone.Prompt = "Fix the build");
        await _ui.Run(() => phone.SendCommand.ExecuteAsync(null));
        Result(Assert.Single(_commands.All()).Id, WorkspaceCommandStatus.Sending, step: "Typing it into VS Code");
        _time.Advance(TimeSpan.FromMinutes(5));
        Tick(phone);
        Assert.True(phone.IsSending);
        Assert.False(phone.SendFailed);
    }

    /// <summary>One tick of the screen's clock (it ticks only while the screen is shown).</summary>
    private void Tick(WorkspaceRemoteViewModel phone) => _ui.Run(() =>
    {
        phone.IsOnScreen = true;
        phone.IsOnScreen = false;
    });

    private void Result(string id, WorkspaceCommandStatus status, string? step = null, string? session = null, string? run = null) =>
        _results.Upsert(id, new WorkspaceResultRecord { CommandId = id, MachineId = "pc", Status = status, Step = step, SessionId = session, UpdatedAt = _time.GetLocalNow(), Run = run });

    private sealed class Answer(bool yes) : Helm.Shell.Services.IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(yes);
    }

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { }
    }
}
