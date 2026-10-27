using Helm.Core.Settings;
using Helm.Modules.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The sessions VS Code windows really run: their claude processes and Claude Code's own session files, instead of
/// guesses from tab names; and Claude Code's busy or idle correcting a state a lost hook left behind.
/// </summary>
public sealed class WorkspaceRunningTests : IAsyncDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("helm-ws-run-");
    private readonly string _pipe = "Helm.Workspace.Test." + Guid.NewGuid().ToString("N");
    private List<(int Pid, int ParentPid, string Name)> _processes = [];
    private WorkspaceEngine? _engine;
    private FakeBridge? _bridge;

    private string SessionsDir => Directory.CreateDirectory(Path.Combine(_dir.FullName, "sessions")).FullName;

    private string Projects => Path.Combine(_dir.FullName, "projects");

    private ClaudeProcesses Processes() => new(() => _processes, SessionsDir);

    private void SessionFile(int pid, string sessionId, string cwd, string status = "idle", DateTimeOffset? at = null) =>
        File.WriteAllText(Path.Combine(SessionsDir, pid + ".json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            pid,
            sessionId,
            cwd,
            status,
            statusUpdatedAt = (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            entrypoint = "claude-vscode",
        }));

    [Fact]
    public void Only_claude_processes_of_the_windows_with_a_session_file_are_read()
    {
        _processes = [(100, 1, "claude.exe"), (101, 2, "claude.exe"), (102, 1, "node.exe"), (103, 1, "claude.exe"), (104, 9, "claude.exe")];
        SessionFile(100, "s-a", @"c:\work\app", "busy");
        SessionFile(101, "s-b", @"c:\work\lib");
        SessionFile(104, "s-other", @"c:\elsewhere");

        var found = Processes().Scan([1, 2]);

        Assert.Equal(["s-a", "s-b"], found.Select(p => p.SessionId).Order());
        var a = found.Single(p => p.SessionId == "s-a");
        Assert.Equal((100, 1, true), (a.Pid, a.HostPid, a.Busy));
        Assert.Equal(@"c:\work\app", a.Cwd);
        Assert.False(found.Single(p => p.SessionId == "s-b").Busy);
    }

    [Fact]
    public void A_session_file_being_rewritten_keeps_the_last_good_read()
    {
        _processes = [(100, 1, "claude.exe")];
        SessionFile(100, "s-a", @"c:\work\app");
        var processes = Processes();
        Assert.Single(processes.Scan([1]));

        File.WriteAllText(Path.Combine(SessionsDir, "100.json"), "");
        Assert.Equal("s-a", Assert.Single(processes.Scan([1])).SessionId);

        // The process is gone: so is the session.
        _processes = [];
        Assert.Empty(processes.Scan([1]));
    }

    [Fact]
    public void A_lost_stop_hook_no_longer_leaves_a_session_working()
    {
        var registry = new SessionRegistry("pc");
        registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.UserPromptSubmit, SessionId = "s1", Cwd = @"c:\work", Prompt = "Hi" });
        var hookAt = registry.Find("s1")!.UpdatedAt;

        // Claude Code's file says busy from when the turn started: the hook is as new, nothing changes.
        registry.Settle("s1", busy: true, hookAt);
        registry.Settle("s1", busy: false, hookAt.AddSeconds(1));
        Assert.Equal(SessionState.Working, registry.Find("s1")!.State);

        // Idle well after the last hook: the Stop hook was lost.
        registry.Settle("s1", busy: false, hookAt.AddSeconds(30));
        Assert.Equal(SessionState.Idle, registry.Find("s1")!.State);

        // Busy again, typed in VS Code while Helm heard nothing.
        registry.Settle("s1", busy: true, DateTimeOffset.Now.AddSeconds(60));
        Assert.Equal(SessionState.Working, registry.Find("s1")!.State);
    }

    [Fact]
    public void Waiting_for_you_is_left_to_the_hooks()
    {
        var registry = new SessionRegistry("pc");
        registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.PermissionRequest, SessionId = "s1", Cwd = @"c:\work", ToolName = "Bash" });
        registry.Settle("s1", busy: false, DateTimeOffset.Now.AddMinutes(5));
        Assert.Equal(SessionState.NeedsPermission, registry.Find("s1")!.State);
    }

    [Fact]
    public async Task A_window_lists_the_sessions_it_runs_whatever_their_folder_or_tab_name()
    {
        var app = Directory.CreateDirectory(Path.Combine(_dir.FullName, "app")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_dir.FullName, "other")).FullName;
        var engine = Start();
        // Two conversations with the same name in the window's folder; only the newer-looking one is not the open one.
        await Converse(engine, "open", app, "Fix the build");
        await Converse(engine, "old", app, "Fix the build");
        // One whose shell is in another folder, running in this window all the same.
        await Converse(engine, "moved", other, "Moved away");

        _processes = [(500, 1, "claude.exe"), (501, 1, "claude.exe")];
        SessionFile(500, "open", app);
        SessionFile(501, "moved", other);
        _bridge = new FakeBridge(_pipe);
        await _bridge.ConnectAsync("app", [app]);
        await _bridge.TabsAsync("app", [app], ("Fix the build", true), ("Moved away", false));
        await WorkspaceBridgeTests.WaitAsync(() => engine.Windows().SingleOrDefault() is { OpenSessions.Count: 2 } w ? w : null);

        var window = engine.Windows().Single();
        Assert.Equal(["open", "moved"], window.OpenSessions);
        Assert.Equal("open", window.ActiveSession);
        Assert.Contains(window.Sessions, s => s.SessionId == "moved");
        Assert.DoesNotContain(engine.OtherSessions(), s => s.SessionId == "moved");
    }

    private WorkspaceEngine Start()
    {
        var settings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.FullName, "pc"))).Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        _engine = WorkspaceEngine.Start(settings, new HelmPaths(_dir.FullName), new PromptSender(new NoKeys(), NullLogger<PromptSender>.Instance),
            NullLogger.Instance, bridgePipe: _pipe, projectsRoot: Projects, processes: Processes());
        return _engine;
    }

    /// <summary>One turn of a conversation whose first prompt (and so its tab's name) is <paramref name="title"/>.</summary>
    private async Task Converse(WorkspaceEngine engine, string id, string cwd, string title)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Projects, ClaudeProjects.ProjectKey(cwd)));
        var path = Path.Combine(dir.FullName, id + ".jsonl");
        File.WriteAllLines(path, [$$"""{"type":"user","cwd":{{System.Text.Json.JsonSerializer.Serialize(cwd)}},"sessionId":"{{id}}"}"""]);
        await engine.HandleHookAsync(new ClaudeHookEvent { Event = ClaudeHookEvent.UserPromptSubmit, SessionId = id, Cwd = cwd, TranscriptPath = path, Prompt = title });
        await engine.HandleHookAsync(new ClaudeHookEvent { Event = ClaudeHookEvent.Stop, SessionId = id, Cwd = cwd, TranscriptPath = path, LastAssistantMessage = "Done" });
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

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null) await _bridge.DisposeAsync();
        if (_engine is not null) await _engine.DisposeAsync();
        try { _dir.Delete(true); } catch (IOException) { }
    }
}
