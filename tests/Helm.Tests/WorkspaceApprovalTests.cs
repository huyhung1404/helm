using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Helm.Core.Desktop;
using Helm.Core.Settings;
using Helm.Modules.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public class PendingRequestTests
{
    [Fact]
    public void A_bash_request_shows_its_command()
    {
        var e = Permission("Bash", """{"command":"git push --force","description":"Push the branch"}""");
        var r = PendingRequest.From(e, "r1", DateTimeOffset.Now, TimeSpan.FromMinutes(10));
        Assert.Equal(PendingKind.Permission, r.Kind);
        Assert.Equal("git push --force\n(Push the branch)", r.Detail);
        Assert.Equal("s1", r.SessionId);
    }

    [Fact]
    public void A_question_lists_its_choices()
    {
        var e = Permission("AskUserQuestion", """
            {"questions":[{"question":"Which colour?","header":"Colour","multiSelect":false,
              "options":[{"label":"Red","description":"Warm"},{"label":"Blue","description":"Cool"}]}]}
            """);
        var r = PendingRequest.From(e, "r1", DateTimeOffset.Now, TimeSpan.FromMinutes(10));
        Assert.Equal(PendingKind.Question, r.Kind);
        var q = Assert.Single(r.Questions);
        Assert.Equal("Which colour?", q.Question);
        Assert.Equal(["Red", "Blue"], q.Options.Select(o => o.Label));
    }

    [Fact]
    public void The_decision_and_the_answer_are_what_claude_code_reads()
    {
        var allow = JsonNode.Parse(PendingRequest.Decision(true))!["hookSpecificOutput"]!;
        Assert.Equal("PermissionRequest", allow["hookEventName"]!.GetValue<string>());
        Assert.Equal("allow", allow["decision"]!["behavior"]!.GetValue<string>());

        var deny = JsonNode.Parse(PendingRequest.Decision(false))!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("deny", deny["behavior"]!.GetValue<string>());
        Assert.Equal("Denied from Helm on the phone.", deny["message"]!.GetValue<string>());

        var answer = JsonNode.Parse(PendingRequest.Answer("""{"questions":[{"question":"Which colour?"}]}""",
            new Dictionary<string, string> { ["Which colour?"] = "Blue" }))!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", answer["behavior"]!.GetValue<string>());
        Assert.Equal("Blue", answer["updatedInput"]!["answers"]!["Which colour?"]!.GetValue<string>());
        // The questions stay: Claude Code checks the input it gets back.
        Assert.Equal("Which colour?", answer["updatedInput"]!["questions"]![0]!["question"]!.GetValue<string>());
    }

    [Fact]
    public void Only_the_permission_hook_may_wait_long()
    {
        var json = ClaudeHookConfig.Install(null, 1, "t");
        var hooks = JsonNode.Parse(json)!["hooks"]!;
        Assert.Equal(ClaudeHookConfig.PermissionTimeoutSeconds, hooks["PermissionRequest"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>());
        Assert.Equal(ClaudeHookConfig.TimeoutSeconds, hooks["Stop"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>());
        // An install from before (3 s everywhere) is installed again.
        var old = json.Replace($"\"timeout\": {ClaudeHookConfig.PermissionTimeoutSeconds}", "\"timeout\": 3");
        Assert.False(ClaudeHookConfig.IsInstalled(old, 1, "t"));
        Assert.True(ClaudeHookConfig.IsInstalled(json, 1, "t"));
    }

    internal static ClaudeHookEvent Permission(string tool, string input, string session = "s1") => new()
    {
        Event = ClaudeHookEvent.PermissionRequest, SessionId = session, Cwd = @"c:\work\app", ToolName = tool, ToolInputJson = input,
    };
}

/// <summary>The PC holds Claude's permission request for the phone, through the real hook listener.</summary>
public sealed class WorkspaceApprovalTests : IAsyncDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("helm-ws-approve-");
    private readonly SettingsStoreFactory _settings;
    private readonly FakeActivity _activity = new();
    private readonly ManualTime _time = new(DateTimeOffset.UtcNow);
    private WorkspaceEngine? _engine;

    public WorkspaceApprovalTests() => _settings = new SettingsStoreFactory(new HelmPaths(_dir.FullName));

    private WorkspaceEngine Start(PhoneApprovals mode, bool linked = true)
    {
        var store = _settings.Get<WorkspaceSettings>(WorkspaceIds.ModuleId);
        store.Update(s => s.ApproveFromPhone = mode);
        _engine = WorkspaceEngine.Start(store, new HelmPaths(_dir.FullName), new PromptSender(new NoKeyboard(), NullLogger<PromptSender>.Instance),
            NullLogger.Instance, _time, "Helm.Workspace.Test." + Guid.NewGuid().ToString("N"), Path.Combine(_dir.FullName, "none"), _activity);
        _engine.PhoneLinked = linked;
        return _engine;
    }

    private async Task<string> PostAsync(string json)
    {
        var token = _settings.Get<WorkspaceSettings>(WorkspaceIds.ModuleId).Current.HookToken;
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _engine!.HookPort);
        var stream = client.GetStream();
        var body = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST {ClaudeHookConfig.UrlPath} HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer {token}\r\nContent-Length: {body.Length}\r\n\r\n"));
        await stream.WriteAsync(body);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
    }

    private const string BashRequest = """{"session_id":"s1","cwd":"c:\\work\\app","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"npm publish"}}""";

    [Fact]
    public async Task The_phone_allows_and_claude_gets_allow()
    {
        var engine = Start(PhoneApprovals.Always);
        var response = PostAsync(BashRequest);
        var pending = await WorkspaceBridgeTests.WaitAsync(() => engine.Registry.Find("s1")?.Pending);
        Assert.Equal("npm publish", pending.Detail);
        Assert.Equal(SessionState.NeedsPermission, engine.Registry.Find("s1")!.State);

        var result = await engine.RunAsync(new WorkspaceCommand
        {
            MachineId = engine.MachineId, Kind = WorkspaceCommandKind.Answer, RequestId = pending.RequestId, Allow = true, Text = "", CreatedAt = DateTimeOffset.Now,
        }, CancellationToken.None);
        Assert.True(result.Status == WorkspaceCommandStatus.Received, result.Error);
        var decision = JsonNode.Parse(await response)!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", decision["behavior"]!.GetValue<string>());
        Assert.Null(engine.Registry.Find("s1")!.Pending);

        // A second answer finds nothing waiting.
        var late = await engine.RunAsync(new WorkspaceCommand
        {
            MachineId = engine.MachineId, Kind = WorkspaceCommandKind.Answer, RequestId = pending.RequestId, Allow = false, Text = "", CreatedAt = DateTimeOffset.Now,
        }, CancellationToken.None);
        Assert.Equal(WorkspaceCommandStatus.Failed, late.Status);
    }

    [Fact]
    public async Task The_phone_answers_a_question()
    {
        var engine = Start(PhoneApprovals.Always);
        var response = PostAsync("""{"session_id":"s1","cwd":"c:\\work\\app","hook_event_name":"PermissionRequest","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Which?","header":"Pick","options":[{"label":"A","description":""},{"label":"B","description":""}]}]}}""");
        var pending = await WorkspaceBridgeTests.WaitAsync(() => engine.Registry.Find("s1")?.Pending);
        Assert.Equal(PendingKind.Question, pending.Kind);
        Assert.Equal(SessionState.Question, engine.Registry.Find("s1")!.State);
        await engine.RunAsync(new WorkspaceCommand
        {
            MachineId = engine.MachineId, Kind = WorkspaceCommandKind.Answer, RequestId = pending.RequestId, Text = "",
            Answers = new Dictionary<string, string> { ["Which?"] = "B" }, CreatedAt = DateTimeOffset.Now,
        }, CancellationToken.None);
        var decision = JsonNode.Parse(await response)!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("B", decision["updatedInput"]!["answers"]!["Which?"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(PhoneApprovals.Off, true, 10)]
    [InlineData(PhoneApprovals.Always, false, 10)]
    [InlineData(PhoneApprovals.WhenAway, true, 0)]
    public async Task VS_Code_asks_at_once_when_the_phone_should_not(PhoneApprovals mode, bool linked, int idleMinutes)
    {
        _activity.LastInputTick = Environment.TickCount64 - (long)TimeSpan.FromMinutes(idleMinutes).TotalMilliseconds;
        Start(mode, linked);
        Assert.Equal("{}", await PostAsync(BashRequest));
    }

    [Fact]
    public async Task Coming_back_to_the_pc_hands_the_request_to_vs_code()
    {
        _activity.LastInputTick = Environment.TickCount64 - (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
        var engine = Start(PhoneApprovals.WhenAway);
        var response = PostAsync(BashRequest);
        await WorkspaceBridgeTests.WaitAsync(() => engine.Registry.Find("s1")?.Pending);
        _activity.LastInputTick = Environment.TickCount64 + 10;
        Assert.Equal("{}", await response);
        Assert.Null(engine.Registry.Find("s1")!.Pending);
    }

    [Fact]
    public async Task An_unanswered_request_goes_back_to_vs_code_when_time_is_up()
    {
        var engine = Start(PhoneApprovals.Always);
        var response = PostAsync(BashRequest);
        await WorkspaceBridgeTests.WaitAsync(() => engine.Registry.Find("s1")?.Pending);
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("{}", await response);
    }

    [Fact]
    public async Task A_disconnected_session_is_never_held()
    {
        var engine = Start(PhoneApprovals.Always);
        engine.SetSessionLink("s1", false);
        Assert.Equal("{}", await PostAsync(BashRequest));
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync();
        _settings.Dispose();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    private sealed class FakeActivity : IUserActivity
    {
        public long LastInputTick { get; set; } = Environment.TickCount64 - 3_600_000;
    }

    private sealed class NoKeyboard : IPromptKeyboard
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

public class WorkspaceNotifierTests
{
    private static WorkspaceSessionRecord Session(string id, SessionState state, PendingRequest? pending = null, string reply = "All green.") => new()
    {
        MachineId = "pc", SessionId = id, WindowFolder = @"c:\work\app", Folder = @"c:\work\app", Title = "Build fix", State = state,
        LastReply = reply, Pending = pending,
    };

    private static readonly WorkspaceHostRecord[] Hosts =
        [new() { MachineId = "pc", MachineName = "Desk", Running = true, Windows = [new HostWindowInfo { Folder = @"c:\work\app", Name = "app" }] }];

    [Fact]
    public void Notifies_what_finished_or_waits_since_last_time_and_nothing_old()
    {
        var notifier = new WorkspaceNotifier();
        // Watching starts: what is already done or waiting is old news.
        Assert.Empty(notifier.Next([Session("a", SessionState.Done), Session("b", SessionState.Working)], Hosts, null));

        var done = Assert.Single(notifier.Next([Session("a", SessionState.Done), Session("b", SessionState.Done, reply: "Tests pass.")], Hosts, null));
        Assert.Equal(NoticeKind.Done, done.Kind);
        Assert.Equal("b", done.SessionId);
        Assert.Equal("Tests pass.", done.Text);
        Assert.Equal("app · Desk", done.GroupTitle);

        var ask = new PendingRequest { RequestId = "r1", SessionId = "b", Kind = PendingKind.Permission, Tool = "Bash", Detail = "npm publish" };
        var permission = Assert.Single(notifier.Next([Session("a", SessionState.Done), Session("b", SessionState.NeedsPermission, ask)], Hosts, null));
        Assert.Equal(NoticeKind.Permission, permission.Kind);
        Assert.Equal("Wants to use Bash: npm publish", permission.Text);
        // The same request is not notified again.
        Assert.Empty(notifier.Next([Session("a", SessionState.Done), Session("b", SessionState.NeedsPermission, ask)], Hosts, null));
    }

    [Fact]
    public void The_conversation_on_screen_is_not_notified()
    {
        var notifier = new WorkspaceNotifier();
        notifier.Next([Session("a", SessionState.Working)], Hosts, null);
        Assert.Empty(notifier.Next([Session("a", SessionState.Done)], Hosts, viewingSessionId: "a"));
    }
}
