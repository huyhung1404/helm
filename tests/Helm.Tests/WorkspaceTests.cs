using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Services;
using Helm.Modules.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public class ClaudeHookEventTests
{
    [Fact]
    public void Parses_a_stop_hook()
    {
        var e = Parse("""{"session_id":"s1","transcript_path":"C:\\t\\s1.jsonl","cwd":"c:\\work\\app","hook_event_name":"Stop","stop_hook_active":false,"last_assistant_message":"Done.","permission_mode":"auto"}""");
        Assert.NotNull(e);
        Assert.Equal(ClaudeHookEvent.Stop, e.Event);
        Assert.Equal("s1", e.SessionId);
        Assert.Equal(@"c:\work\app", e.Cwd);
        Assert.Equal("Done.", e.LastAssistantMessage);
        Assert.Equal("auto", e.PermissionMode);
        Assert.False(e.StopHookActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"hook_event_name":"Stop"}""")]
    [InlineData("""{"session_id":"s1"}""")]
    [InlineData("not json")]
    public void Ignores_what_is_not_a_usable_hook(string json) => Assert.Null(Parse(json));

    [Fact]
    public void Ignores_unknown_fields_and_wrong_types()
    {
        var e = Parse("""{"session_id":"s1","hook_event_name":"UserPromptSubmit","prompt":42,"future_field":{"a":1}}""");
        Assert.NotNull(e);
        Assert.Null(e.Prompt);
    }

    internal static ClaudeHookEvent? Parse(string json) => ClaudeHookEvent.Parse(Encoding.UTF8.GetBytes(json));
}

public class SessionRegistryTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));

    private static ClaudeHookEvent Hook(string name, string session = "s1", string? prompt = null, string? reply = null,
        string? tool = null, bool stopActive = false, string cwd = @"c:\work\app", string? notification = null) => new()
    {
        Event = name,
        SessionId = session,
        Cwd = cwd,
        TranscriptPath = $@"C:\t\{session}.jsonl",
        Prompt = prompt,
        LastAssistantMessage = reply,
        ToolName = tool,
        StopHookActive = stopActive,
        NotificationType = notification,
    };

    [Fact]
    public void Follows_a_turn_from_prompt_to_answer()
    {
        var registry = new SessionRegistry("pc-1", _time);
        registry.Apply(Hook(ClaudeHookEvent.SessionStart));
        Assert.Equal(SessionState.Idle, registry.Find("s1")!.State);

        registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, prompt: "Fix the build"));
        var working = registry.Find("s1")!;
        Assert.Equal(SessionState.Working, working.State);
        Assert.Equal("Fix the build", working.LastPrompt);
        Assert.Equal("pc-1", working.MachineId);

        registry.Apply(Hook(ClaudeHookEvent.PermissionRequest, tool: "Bash"));
        Assert.Equal(SessionState.NeedsPermission, registry.Find("s1")!.State);
        Assert.Equal("Bash", registry.Find("s1")!.Waiting);

        // Allowed in VS Code: the tool ran, Claude is back at work.
        registry.Apply(Hook(ClaudeHookEvent.PostToolUse, tool: "Bash"));
        Assert.Equal(SessionState.Working, registry.Find("s1")!.State);
        Assert.Null(registry.Find("s1")!.Waiting);

        registry.Apply(Hook(ClaudeHookEvent.Stop, reply: "Build fixed."));
        var done = registry.Find("s1")!;
        Assert.Equal(SessionState.Done, done.State);
        Assert.Equal("Build fixed.", done.LastReply);
    }

    [Fact]
    public void A_question_waits_for_you_until_it_is_answered()
    {
        var registry = new SessionRegistry("pc-1", _time);
        registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, prompt: "Plan it"));
        registry.Apply(Hook(ClaudeHookEvent.PreToolUse, tool: "AskUserQuestion"));
        Assert.Equal(SessionState.Question, registry.Find("s1")!.State);
        Assert.True(registry.Find("s1")!.NeedsYou);
        registry.Apply(Hook(ClaudeHookEvent.PostToolUse, tool: "AskUserQuestion"));
        Assert.Equal(SessionState.Working, registry.Find("s1")!.State);
    }

    [Fact]
    public void A_stop_hook_that_made_claude_continue_is_not_the_end()
    {
        var registry = new SessionRegistry("pc-1", _time);
        registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, prompt: "Go"));
        registry.Apply(Hook(ClaudeHookEvent.Stop, reply: "half", stopActive: true));
        Assert.Equal(SessionState.Working, registry.Find("s1")!.State);
    }

    [Fact]
    public void A_permission_notification_counts_when_no_permission_hook_came()
    {
        var registry = new SessionRegistry("pc-1", _time);
        registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, prompt: "Go"));
        registry.Apply(Hook(ClaudeHookEvent.Notification, notification: "idle_prompt"));
        Assert.Equal(SessionState.Working, registry.Find("s1")!.State);
        registry.Apply(Hook(ClaudeHookEvent.Notification, notification: "permission_prompt"));
        Assert.Equal(SessionState.NeedsPermission, registry.Find("s1")!.State);
    }

    [Fact]
    public void A_question_stays_a_question_when_claude_code_also_notifies()
    {
        var registry = new SessionRegistry("pc-1", _time);
        registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, prompt: "Ask me"));
        registry.Apply(Hook(ClaudeHookEvent.PermissionRequest, tool: "AskUserQuestion"));
        registry.Apply(Hook(ClaudeHookEvent.Notification, notification: "permission_prompt"));
        Assert.Equal(SessionState.Question, registry.Find("s1")!.State);
        Assert.Equal("A question for you", registry.Find("s1")!.Waiting);
    }

    [Fact]
    public void Ended_sessions_leave_the_list_after_a_while()
    {
        var registry = new SessionRegistry("pc-1", _time);
        registry.Apply(Hook(ClaudeHookEvent.SessionStart));
        registry.Apply(Hook(ClaudeHookEvent.SessionEnd));
        Assert.Equal(SessionState.Ended, Assert.Single(registry.Sessions).State);
        _time.Advance(SessionRegistry.KeepEnded + TimeSpan.FromMinutes(1));
        Assert.Empty(registry.Sessions);
    }

    [Fact]
    public void Unknown_events_change_nothing_and_raise_nothing()
    {
        var registry = new SessionRegistry("pc-1", _time);
        var raised = 0;
        registry.Changed += (_, _) => raised++;
        registry.Apply(Hook("SubagentStop"));
        Assert.Empty(registry.Sessions);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void A_typed_prompt_is_confirmed_by_the_hook_with_the_same_text()
    {
        var registry = new SessionRegistry("pc-1", _time);
        registry.Apply(Hook(ClaudeHookEvent.SessionStart));
        PromptReceipt? receipt = null;
        registry.PromptReceived += (_, r) => receipt = r;
        registry.ExpectPrompt("cmd-1", "s1", @"c:\work\app", "Line one\r\nLine two  ", TimeSpan.FromSeconds(30));

        registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, prompt: "Something else"));
        Assert.Null(receipt);
        registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, prompt: "Line one\nLine two"));
        Assert.Equal(new PromptReceipt("cmd-1", "s1"), receipt);
    }

    [Fact]
    public void A_new_session_is_confirmed_by_the_first_matching_prompt_in_its_folder()
    {
        var registry = new SessionRegistry("pc-1", _time);
        PromptReceipt? receipt = null;
        registry.PromptReceived += (_, r) => receipt = r;
        registry.ExpectPrompt("cmd-2", null, @"C:\Work\App\", "Hello", TimeSpan.FromSeconds(30));

        registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, session: "other", prompt: "Hello", cwd: @"c:\elsewhere"));
        Assert.Null(receipt);
        registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, session: "new", prompt: "Hello", cwd: @"c:\work\app"));
        Assert.Equal(new PromptReceipt("cmd-2", "new"), receipt);
    }

    [Fact]
    public void An_expired_expectation_is_never_confirmed()
    {
        var registry = new SessionRegistry("pc-1", _time);
        var confirmed = false;
        registry.PromptReceived += (_, _) => confirmed = true;
        registry.ExpectPrompt("cmd-3", "s1", @"c:\work\app", "Late", TimeSpan.FromSeconds(30));
        _time.Advance(TimeSpan.FromMinutes(1));
        registry.Apply(Hook(ClaudeHookEvent.UserPromptSubmit, prompt: "Late"));
        Assert.False(confirmed);
    }

    [Fact]
    public void Titles_update_only_when_they_change()
    {
        var registry = new SessionRegistry("pc-1", _time);
        registry.Apply(Hook(ClaudeHookEvent.SessionStart));
        var raised = 0;
        registry.Changed += (_, _) => raised++;
        registry.SetTitle("s1", "Fix the build");
        registry.SetTitle("s1", "Fix the build");
        registry.SetTitle("missing", "x");
        Assert.Equal(1, raised);
        Assert.Equal("Fix the build", registry.Find("s1")!.Title);
    }
}

public class ClaudeHookConfigTests
{
    // The user's settings: another notifier's hooks, permissions and a model, like a real ~/.claude/settings.json.
    private const string UserSettings = """
        {
          "permissions": { "allow": ["Bash(git status)"] },
          "model": "opus",
          "hooks": {
            "Stop": [
              { "hooks": [ { "type": "command", "command": "powershell -File notifier-on-stop.ps1" } ] }
            ],
            "PreToolUse": [
              { "matcher": "AskUserQuestion", "hooks": [ { "type": "command", "command": "notifier-on-question.ps1" } ] }
            ]
          }
        }
        """;

    [Fact]
    public void Install_adds_one_helm_hook_per_event_and_keeps_everything_else()
    {
        var json = ClaudeHookConfig.Install(UserSettings, 47821, "tok");
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal("Bash(git status)", root["permissions"]!["allow"]![0]!.GetValue<string>());
        var hooks = root["hooks"]!.AsObject();
        foreach (var name in ClaudeHookEvent.Subscribed) Assert.True(hooks.ContainsKey(name), name);
        // The notifier's own entries are still first and untouched.
        Assert.Equal("powershell -File notifier-on-stop.ps1", hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("notifier-on-question.ps1", hooks["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        var ours = hooks["Stop"]![1]!["hooks"]![0]!;
        Assert.Equal("http", ours["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:47821/helm/workspace/hook", ours["url"]!.GetValue<string>());
        Assert.Equal("Bearer tok", ours["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Equal("AskUserQuestion", hooks["PreToolUse"]![1]!["matcher"]!.GetValue<string>());
        Assert.True(ClaudeHookConfig.IsInstalled(json, 47821, "tok"));
    }

    [Fact]
    public void Install_twice_or_on_a_new_port_never_duplicates()
    {
        var once = ClaudeHookConfig.Install(UserSettings, 47821, "tok");
        var twice = ClaudeHookConfig.Install(once, 47821, "tok");
        Assert.Equal(once, twice);
        var moved = ClaudeHookConfig.Install(twice, 50000, "tok2");
        Assert.True(ClaudeHookConfig.IsInstalled(moved, 50000, "tok2"));
        Assert.False(ClaudeHookConfig.IsInstalled(moved, 47821, "tok"));
        Assert.Equal(2, JsonNode.Parse(moved)!["hooks"]!["Stop"]!.AsArray().Count);
    }

    [Fact]
    public void Remove_leaves_the_users_settings_as_they_were()
    {
        var installed = ClaudeHookConfig.Install(UserSettings, 47821, "tok");
        var removed = ClaudeHookConfig.Remove(installed)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(UserSettings), JsonNode.Parse(removed)));
        Assert.Null(ClaudeHookConfig.Remove(removed));
    }

    [Fact]
    public void Remove_drops_the_hooks_object_helm_created()
    {
        var installed = ClaudeHookConfig.Install("""{"model":"opus"}""", 1234, "t");
        var removed = ClaudeHookConfig.Remove(installed)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"model":"opus"}"""), JsonNode.Parse(removed)));
    }

    [Fact]
    public void Never_touches_http_hooks_that_are_not_helms()
    {
        const string others = """
            {"hooks":{"Stop":[{"hooks":[
              {"type":"http","url":"https://example.com/helm/workspace/hook"},
              {"type":"http","url":"http://127.0.0.1:9000/my-own-hook"},
              {"type":"command","command":"curl http://127.0.0.1:1/helm/workspace/hook"}
            ]}]}}
            """;
        Assert.Null(ClaudeHookConfig.Remove(others));
        var installed = ClaudeHookConfig.Install(others, 1234, "t");
        var removed = ClaudeHookConfig.Remove(installed)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(others), JsonNode.Parse(removed)));
    }

    [Fact]
    public void An_event_list_left_empty_by_removing_helms_hook_goes_away()
    {
        // Helm cannot tell an empty list you wrote from one it emptied; an empty list does nothing either way.
        var removed = ClaudeHookConfig.Remove(ClaudeHookConfig.Install("""{"hooks":{"Notification":[]}}""", 1, "t"))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{}"), JsonNode.Parse(removed)));
    }

    [Fact]
    public void Install_starts_from_nothing_and_refuses_what_is_not_an_object()
    {
        Assert.True(ClaudeHookConfig.IsInstalled(ClaudeHookConfig.Install(null, 1, "t"), 1, "t"));
        Assert.True(ClaudeHookConfig.IsInstalled(ClaudeHookConfig.Install("", 1, "t"), 1, "t"));
        Assert.ThrowsAny<JsonException>(() => ClaudeHookConfig.Install("[1,2]", 1, "t"));
        Assert.ThrowsAny<JsonException>(() => ClaudeHookConfig.Install("{ broken", 1, "t"));
    }

    [Fact]
    public void Rewrite_backs_up_once_and_replaces_the_file_whole()
    {
        var dir = Directory.CreateTempSubdirectory("helm-hooks-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, UserSettings);
            Assert.True(ClaudeHookConfig.Rewrite(path, json => ClaudeHookConfig.Install(json, 1, "t")));
            Assert.Equal(UserSettings, File.ReadAllText(path + ".helm-backup"));
            Assert.False(ClaudeHookConfig.Rewrite(path, json => ClaudeHookConfig.IsInstalled(json, 1, "t") ? null : ClaudeHookConfig.Install(json, 1, "t")));
            Assert.True(ClaudeHookConfig.Rewrite(path, ClaudeHookConfig.Remove));
            // The backup is the file before Helm's first change; later changes never overwrite it.
            Assert.Equal(UserSettings, File.ReadAllText(path + ".helm-backup"));
            Assert.False(File.Exists(path + ".helm-tmp"));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

public class HookListenerTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Accepts_a_hook_with_the_token_and_answers_at_once()
    {
        var received = new TaskCompletionSource<ClaudeHookEvent>();
        await using var listener = HookListener.Start(0, Token, e => { received.TrySetResult(e); return Task.FromResult<string?>(null); }, NullLogger.Instance);
        var (status, body) = await PostAsync(listener.Port, ClaudeHookConfig.UrlPath, "Bearer " + Token,
            """{"session_id":"s1","hook_event_name":"UserPromptSubmit","prompt":"Xin chào, thử tiếng Việt"}""");
        Assert.Equal(200, status);
        Assert.Equal("{}", body);
        var hook = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Xin chào, thử tiếng Việt", hook.Prompt);
    }

    [Theory]
    [InlineData("Bearer wrong", null, ClaudeHookConfig.UrlPath, 401)]
    [InlineData(null, null, ClaudeHookConfig.UrlPath, 401)]
    [InlineData("Bearer " + Token, "https://evil.example", ClaudeHookConfig.UrlPath, 403)]
    [InlineData("Bearer " + Token, null, "/other", 404)]
    public async Task Refuses_what_is_not_claude(string? auth, string? origin, string path, int expected)
    {
        var count = 0;
        await using var listener = HookListener.Start(0, Token, _ => { Interlocked.Increment(ref count); return Task.FromResult<string?>(null); }, NullLogger.Instance);
        var (status, _) = await PostAsync(listener.Port, path, auth, """{"session_id":"s1","hook_event_name":"Stop"}""", origin);
        Assert.Equal(expected, status);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A_taken_port_is_reported()
    {
        await using var first = HookListener.Start(0, Token, _ => Task.FromResult<string?>(null), NullLogger.Instance);
        Assert.Throws<SocketException>(() => HookListener.Start(first.Port, Token, _ => Task.FromResult<string?>(null), NullLogger.Instance));
    }

    private static async Task<(int Status, string Body)> PostAsync(int port, string path, string? auth, string json, string? origin = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        var body = Encoding.UTF8.GetBytes(json);
        var head = new StringBuilder($"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n");
        if (auth is not null) head.Append($"Authorization: {auth}\r\n");
        if (origin is not null) head.Append($"Origin: {origin}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        await stream.WriteAsync(body);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var status = int.Parse(response.Split(' ')[1]);
        return (status, response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
    }
}

public class VsCodeWindowsTests
{
    [Theory]
    [InlineData("alpha - Visual Studio Code", "alpha", true)]
    [InlineData("Claude Code - alpha - Visual Studio Code", "alpha", true)]
    [InlineData("● README.md - alpha - Visual Studio Code", "alpha", true)]
    [InlineData("README.md - alpha - Work - Visual Studio Code", "alpha", true)]
    [InlineData("helm (Workspace) - Visual Studio Code", "helm", true)]
    [InlineData("alpha - Visual Studio Code - Insiders", "alpha", true)]
    [InlineData("alphabet - Visual Studio Code", "alpha", false)]
    [InlineData("alpha - Cursor", "alpha", false)]
    [InlineData("alpha - Visual Studio Code", "", false)]
    [InlineData("Download Visual Studio Code - alpha - Google Chrome", "alpha", false)]
    public void Matches_the_workspace_in_the_title(string title, string name, bool expected) =>
        Assert.Equal(expected, VsCodeWindows.Matches(title, name));

    [Fact]
    public void Two_windows_with_the_same_name_are_ambiguous()
    {
        (nint, string)[] windows = [(1, "a - x - Visual Studio Code"), (2, "b - x - Visual Studio Code"), (3, "y - Visual Studio Code")];
        Assert.Equal(0, VsCodeWindows.Find(windows, "x"));
        Assert.Equal(3, VsCodeWindows.Find(windows, "y"));
        Assert.Equal(0, VsCodeWindows.Find(windows, "z"));
    }
}

public class TranscriptReaderTests
{
    [Fact]
    public void Reads_the_title_and_the_conversation_without_tools_or_side_chains()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,
            [
                """{"type":"user","message":{"role":"user","content":"Fix the build"},"timestamp":"2026-10-13T04:00:00Z"}""",
                """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"Looking."}]}}""",
                """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{}}]}}""",
                """{"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Fixed it."}]}}""",
                """{"type":"user","isSidechain":true,"message":{"content":"agent chatter"}}""",
                """{"type":"user","message":{"content":"<command-name>/model</command-name>"}}""",
                """{"type":"ai-title","aiTitle":"Build fix","sessionId":"s1"}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"half a li""",
            ]);
            var tail = TranscriptReader.Read(path);
            Assert.Equal("Build fix", tail.Title);
            Assert.Collection(tail.Turns,
                t => { Assert.True(t.FromUser); Assert.Equal("Fix the build", t.Text); Assert.NotNull(t.At); },
                t => { Assert.False(t.FromUser); Assert.Equal("Looking.\n\nFixed it.", t.Text); });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_title_you_set_wins_and_a_missing_file_gives_nothing()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, ["""{"type":"ai-title","aiTitle":"Auto"}""", """{"type":"custom-title","customTitle":"Mine"}"""]);
            Assert.Equal("Mine", TranscriptReader.Read(path).Title);
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Equal(new TranscriptTail("", []).Title, TranscriptReader.Read(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".jsonl")).Title);
    }
}

public class BackgroundActivitiesTests
{
    [Fact]
    public async Task Lists_an_activity_until_it_ends_and_stop_runs_its_owner()
    {
        var activities = new BackgroundActivities();
        var changes = 0;
        activities.Changed += (_, _) => changes++;
        var stopped = false;
        var activity = activities.Start("tool", "Tool", "Listening", () => { stopped = true; return Task.CompletedTask; });
        Assert.Same(activity, Assert.Single(activities.Current));
        activity.Detail = "Busy";
        activity.Detail = "Busy";
        await activity.StopAsync();
        Assert.True(stopped);
        activity.Dispose();
        activity.Dispose();
        Assert.Empty(activities.Current);
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task A_failing_stop_never_throws()
    {
        var activity = new BackgroundActivities().Start("tool", "Tool", "", () => throw new InvalidOperationException());
        await activity.StopAsync();
    }

    [Fact]
    public void The_general_page_rows_follow_the_list()
    {
        var activities = new BackgroundActivities();
        var vm = new Helm.Shell.ViewModels.BackgroundActivitiesViewModel(activities, new InlineUi());
        Assert.False(vm.HasItems);
        var a = activities.Start("tool", "Tool", "one", () => Task.CompletedTask);
        Assert.Equal("one", Assert.Single(vm.Items).Detail);
        a.Detail = "two";
        Assert.Equal("two", Assert.Single(vm.Items).Detail);
        a.Dispose();
        Assert.Empty(vm.Items);
    }
}

public class WorkspaceBridgeTests
{
    [Fact]
    public async Task A_window_connects_answers_requests_and_leaves()
    {
        var pipe = "Helm.Workspace.Test." + Guid.NewGuid().ToString("N");
        await using var host = new BridgeHost(pipe, NullLogger.Instance);
        await using var client = new FakeBridge(pipe);
        await client.ConnectAsync("alpha", [@"c:\work\alpha"]);
        var window = await WaitAsync(() => host.Windows.SingleOrDefault());
        Assert.Equal("alpha", window.Info.Name);
        Assert.Equal(@"c:\work\alpha", Assert.Single(window.Info.Folders));

        await window.RequestAsync("open", "s1");
        Assert.Equal(("open", "s1"), Assert.Single(client.Requests));

        client.FailNext = "Claude Code is not installed";
        var ex = await Assert.ThrowsAsync<BridgeException>(() => window.RequestAsync("focus"));
        Assert.Equal("Claude Code is not installed", ex.Message);

        await client.DisposeAsync();
        await WaitAsync(() => host.Windows.Count == 0 ? (object)true : null);
    }

    [Fact]
    public void Pipe_name_follows_the_data_folder_like_the_extension()
    {
        var a = WorkspaceEndpoint.PipeName(new Helm.Core.Settings.HelmPaths(@"C:\Users\x\AppData\Local\Helm"));
        var b = WorkspaceEndpoint.PipeName(new Helm.Core.Settings.HelmPaths(@"c:\users\x\appdata\local\helm\"));
        var c = WorkspaceEndpoint.PipeName(new Helm.Core.Settings.HelmPaths(@"C:\test\data"));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Matches("^Helm\\.Workspace\\.[0-9A-F]{16}$", a);
        // The extension computes the same name (Bridge/extension.js → pipeName()).
        var js = BridgeInstaller.Resource("extension.js");
        Assert.Contains("'\\\\\\\\.\\\\pipe\\\\Helm.Workspace.'", js);
        Assert.Contains(".slice(0, 16).toUpperCase()", js);
    }

    [Fact]
    public void The_vsix_holds_the_manifest_and_the_extension()
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(BridgeInstaller.BuildVsix()));
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("[Content_Types].xml", names);
        Assert.Contains("extension.vsixmanifest", names);
        Assert.Contains("extension/package.json", names);
        Assert.Contains("extension/extension.js", names);
        using var manifest = new StreamReader(zip.GetEntry("extension.vsixmanifest")!.Open());
        Assert.Contains($"Version=\"{BridgeInstaller.Version}\" Publisher=\"helm\"", manifest.ReadToEnd());
    }

    internal static async Task<T> WaitLongAsync<T>(Func<T?> probe) where T : class
    {
        for (var i = 0; i < 120; i++)
        {
            if (probe() is { } value) return value;
            await Task.Delay(500);
        }
        throw new TimeoutException();
    }

    internal static async Task<T> WaitAsync<T>(Func<T?> probe) where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (probe() is { } value) return value;
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }
}

/// <summary>Plays Helm Bridge: says hello, records requests and answers them.</summary>
internal sealed class FakeBridge(string pipe) : IAsyncDisposable
{
    private readonly NamedPipeClientStream _stream = new(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
    private StreamWriter? _writer;
    private Task? _loop;

    public List<(string Op, string? Session)> Requests { get; } = [];

    public string? FailNext { get; set; }

    public async Task ConnectAsync(string name, string[] folders, string sendKey = "enter")
    {
        await _stream.ConnectAsync(5000);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await _writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "hello", protocol = 1, pid = 1, name, folders, sendKey, claudeTabs = 0, focused = false }));
        _loop = Task.Run(async () =>
        {
            using var reader = new StreamReader(_stream, new UTF8Encoding(false));
            while (await reader.ReadLineAsync() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var op = r.GetProperty("op").GetString()!;
                var session = r.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                lock (Requests) Requests.Add((op, session));
                var fail = FailNext;
                FailNext = null;
                await _writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "reply", id = r.GetProperty("id").GetString(), ok = fail is null, error = fail }));
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (Exception) { }
        }
    }
}

public class PromptSenderTests
{
    [Fact]
    public async Task Types_each_line_with_shift_enter_between_and_submits_in_the_window()
    {
        var (host, bridge, window) = await ConnectAsync();
        await using var _ = host;
        await using var __ = bridge;
        var keys = new FakeKeyboard(target: 42, title: "Claude Code - alpha - Visual Studio Code", previous: 7);
        await new PromptSender(keys, NullLogger<PromptSender>.Instance).SendAsync(window, "s1", "Line one\r\nDòng hai", null, CancellationToken.None);

        Assert.Equal(["type:Line one", "newline", "type:Dòng hai", "submit:enter"], keys.Log);
        Assert.Equal([("open", "s1"), ("focus", null)], bridge.Requests);
        Assert.Equal(7, keys.ActivatedAfter);
    }

    [Fact]
    public async Task A_new_session_switches_model_first_and_respects_ctrl_enter()
    {
        var (host, bridge, window) = await ConnectAsync(sendKey: "ctrl+enter");
        await using var _ = host;
        await using var __ = bridge;
        var keys = new FakeKeyboard(target: 42, title: "alpha - Visual Studio Code", previous: 0);
        await new PromptSender(keys, NullLogger<PromptSender>.Instance).SendAsync(window, null, "Hi", "sonnet", CancellationToken.None);

        Assert.Equal(["type:/model sonnet", "submit:ctrl", "type:Hi", "submit:ctrl"], keys.Log);
        Assert.Equal("new", bridge.Requests[0].Op);
        Assert.Equal("focusEditor", bridge.Requests[1].Op);
    }

    [Fact]
    public async Task Presses_enter_again_until_claude_takes_the_prompt()
    {
        var (host, bridge, window) = await ConnectAsync();
        await using var _ = host;
        await using var __ = bridge;
        var keys = new FakeKeyboard(target: 42, title: "alpha - Visual Studio Code", previous: 0);
        var never = new TaskCompletionSource().Task;
        await new PromptSender(keys, NullLogger<PromptSender>.Instance).SendAsync(window, "s1", "Go", null, CancellationToken.None, never);
        Assert.Equal(["type:Go", "submit:enter", "wait", "submit:enter", "wait", "submit:enter"], keys.Log);

        var confirmed = new FakeKeyboard(target: 42, title: "alpha - Visual Studio Code", previous: 0);
        await new PromptSender(confirmed, NullLogger<PromptSender>.Instance).SendAsync(window, "s1", "Go", null, CancellationToken.None, Task.CompletedTask);
        Assert.Equal(["type:Go", "submit:enter", "wait"], confirmed.Log);
    }

    [Fact]
    public async Task Stops_typing_when_another_window_comes_to_the_front()
    {
        var (host, bridge, window) = await ConnectAsync();
        await using var _ = host;
        await using var __ = bridge;
        var keys = new FakeKeyboard(target: 42, title: "alpha - Visual Studio Code", previous: 0) { StealFocusAfterTypes = 1 };
        var ex = await Assert.ThrowsAsync<PromptSendException>(() =>
            new PromptSender(keys, NullLogger<PromptSender>.Instance).SendAsync(window, "s1", "one\ntwo", null, CancellationToken.None));
        Assert.Contains("Another window came to the front", ex.Message);
        Assert.Equal(["type:one"], keys.Log);
    }

    [Fact]
    public async Task Sends_nothing_when_the_window_cannot_come_forward_or_a_key_is_held()
    {
        var (host, bridge, window) = await ConnectAsync();
        await using var _ = host;
        await using var __ = bridge;
        var refused = new FakeKeyboard(target: 42, title: "alpha - Visual Studio Code", previous: 0) { RefuseActivate = true };
        await Assert.ThrowsAsync<PromptSendException>(() =>
            new PromptSender(refused, NullLogger<PromptSender>.Instance).SendAsync(window, "s1", "x", null, CancellationToken.None));
        Assert.Empty(refused.Log);

        var held = new FakeKeyboard(target: 42, title: "alpha - Visual Studio Code", previous: 0) { Modifier = true };
        var ex = await Assert.ThrowsAsync<PromptSendException>(() =>
            new PromptSender(held, NullLogger<PromptSender>.Instance).SendAsync(window, "s1", "x", null, CancellationToken.None));
        Assert.Contains("held down", ex.Message);
        Assert.Empty(held.Log);
    }

    [Fact]
    public async Task A_missing_window_is_reported()
    {
        var (host, bridge, window) = await ConnectAsync();
        await using var _ = host;
        await using var __ = bridge;
        var keys = new FakeKeyboard(target: 42, title: "beta - Visual Studio Code", previous: 0);
        var ex = await Assert.ThrowsAsync<PromptSendException>(() =>
            new PromptSender(keys, NullLogger<PromptSender>.Instance).SendAsync(window, "s1", "x", null, CancellationToken.None));
        Assert.Contains("was not found", ex.Message);
    }

    private static async Task<(BridgeHost Host, FakeBridge Bridge, BridgeConnection Window)> ConnectAsync(string sendKey = "enter")
    {
        var pipe = "Helm.Workspace.Test." + Guid.NewGuid().ToString("N");
        var host = new BridgeHost(pipe, NullLogger.Instance);
        var bridge = new FakeBridge(pipe);
        await bridge.ConnectAsync("alpha", [@"c:\work\alpha"], sendKey);
        var window = await WorkspaceBridgeTests.WaitAsync(() => host.Windows.SingleOrDefault());
        return (host, bridge, window);
    }

    private sealed class FakeKeyboard(nint target, string title, nint previous) : IPromptKeyboard
    {
        private nint _foreground = previous;
        private int _types;

        public List<string> Log { get; } = [];
        public bool RefuseActivate { get; init; }
        public bool Modifier { get; init; }
        public int StealFocusAfterTypes { get; init; } = int.MaxValue;
        public nint ActivatedAfter { get; private set; }

        public IReadOnlyList<(nint Handle, string Title)> TopWindows() => [(target, title), (99, "notes - Notepad")];
        public nint Foreground() => _foreground;

        public bool ForceActivate(nint hwnd)
        {
            if (RefuseActivate) return false;
            _foreground = hwnd;
            return true;
        }

        public void Activate(nint hwnd) => ActivatedAfter = hwnd;
        public bool ModifierDown() => Modifier;

        public void Type(string text)
        {
            Log.Add("type:" + text);
            if (++_types >= StealFocusAfterTypes) _foreground = 99;
        }

        public void NewLine() => Log.Add("newline");
        public void Submit(bool ctrl) => Log.Add(ctrl ? "submit:ctrl" : "submit:enter");
        public Task Pause(int milliseconds, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> WaitFor(Task task, int milliseconds, CancellationToken ct)
        {
            Log.Add("wait");
            return Task.FromResult(task.IsCompleted);
        }
    }
}

public class WorkspaceEngineTests
{
    [Theory]
    [InlineData(@"c:\tmp\hws-spike\ws\alpha", "c--tmp-hws-spike-ws-alpha")]
    [InlineData(@"C:\Project\Bee Smooth\", "C--Project-Bee-Smooth")]
    [InlineData(@"c:\Project\huyhung1404\Packages", "c--Project-huyhung1404-Packages")]
    public void Claude_names_a_project_folder_after_its_path(string folder, string key) =>
        Assert.Equal(key, ClaudeProjects.ProjectKey(folder));

    [Fact]
    public void Sessions_from_before_helm_started_are_listed_with_their_last_answer()
    {
        var root = Directory.CreateTempSubdirectory("helm-projects-");
        try
        {
            // Both spellings of the drive letter exist on real machines.
            var upper = Directory.CreateDirectory(Path.Combine(root.FullName, "C--work-app"));
            var lower = Directory.CreateDirectory(Path.Combine(root.FullName, "c--work-app"));
            var other = Directory.CreateDirectory(Path.Combine(root.FullName, "c--work-apple"));
            var done = Guid.NewGuid().ToString();
            var asked = Guid.NewGuid().ToString();
            var old = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(upper.FullName, done + ".jsonl"),
            [
                """{"type":"user","message":{"content":"Fix it"}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Fixed."}]}}""",
                """{"type":"ai-title","aiTitle":"Fix"}""",
            ]);
            File.WriteAllLines(Path.Combine(lower.FullName, asked + ".jsonl"), ["""{"type":"user","message":{"content":"Still going?"}}"""]);
            File.WriteAllLines(Path.Combine(lower.FullName, old + ".jsonl"), ["""{"type":"user","message":{"content":"Old"}}"""]);
            File.SetLastWriteTimeUtc(Path.Combine(lower.FullName, old + ".jsonl"), DateTime.UtcNow.AddDays(-3));
            File.WriteAllLines(Path.Combine(other.FullName, Guid.NewGuid() + ".jsonl"), ["""{"type":"user","message":{"content":"Not this folder"}}"""]);
            File.WriteAllText(Path.Combine(lower.FullName, "not-a-session.jsonl"), "{}");

            var registry = new SessionRegistry("pc-1");
            Assert.Equal(2, WorkspaceEngine.Seed(registry, @"c:\work\app", DateTimeOffset.Now.AddDays(-1), root.FullName));
            var fixedOne = registry.Find(done)!;
            Assert.Equal(SessionState.Done, fixedOne.State);
            Assert.Equal("Fix", fixedOne.Title);
            Assert.Equal("Fixed.", fixedOne.LastReply);
            Assert.Equal("pc-1", fixedOne.MachineId);
            Assert.Equal(SessionState.Idle, registry.Find(asked)!.State);
            Assert.Null(registry.Find(old));

            // What the hooks said wins over an older record.
            registry.Apply(new ClaudeHookEvent { Event = ClaudeHookEvent.UserPromptSubmit, SessionId = done, Cwd = @"c:\work\app", Prompt = "More" });
            Assert.Equal(0, WorkspaceEngine.Seed(registry, @"c:\work\app", DateTimeOffset.Now.AddDays(-1), root.FullName));
            Assert.Equal(SessionState.Working, registry.Find(done)!.State);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData(@"c:\work\app", @"C:\Work\App", true)]
    [InlineData(@"c:\work\app\src", @"c:\work\app\", true)]
    [InlineData(@"c:\work\apple", @"c:\work\app", false)]
    [InlineData(@"c:/work/app", @"c:\work\app", true)]
    [InlineData("", @"c:\work\app", false)]
    public void A_session_belongs_to_the_window_that_has_its_folder(string folder, string root, bool expected) =>
        Assert.Equal(expected, WorkspaceEngine.IsUnder(folder, root));
}
