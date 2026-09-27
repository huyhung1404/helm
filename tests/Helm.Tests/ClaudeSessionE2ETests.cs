using System.Text;
using Helm.Core.Processes;
using Helm.Modules.ClaudeChat.Chat;
using Helm.Modules.ClaudeChat.Cli;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// Runs the real Claude Code CLI (Haiku, a few cents per run). Skipped unless HELM_CLAUDE_E2E=1, so CI and normal
/// test runs never spend quota: <c>$env:HELM_CLAUDE_E2E=1; dotnet test --filter ClaudeSessionE2E</c>.
/// </summary>
public class ClaudeSessionE2ETests
{
    private static readonly TimeSpan s_turnTimeout = TimeSpan.FromSeconds(90);

    private static bool Enabled => Environment.GetEnvironmentVariable("HELM_CLAUDE_E2E") == "1";

    [Fact]
    public async Task Conversation_interrupt_and_permission_round_trip()
    {
        if (!Enabled) return;
        var cli = ClaudeCliLocator.Find() ?? throw new InvalidOperationException("Claude Code is not installed.");
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "helm-e2e-" + Guid.NewGuid().ToString("N"))).FullName;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var outside = Path.Combine(localAppData, "helm-e2e-allowed-" + Guid.NewGuid().ToString("N") + ".txt");
        var secondOutside = Path.Combine(localAppData, "helm-e2e-denied-" + Guid.NewGuid().ToString("N") + ".txt");
        var launcher = new ChildProcessLauncher(NullLogger<ChildProcessLauncher>.Instance);
        try
        {
            await using var session = await ClaudeSession.StartAsync(launcher, cli, new ClaudeSessionOptions(folder) { Model = "haiku" }, NullLogger.Instance, CancellationToken.None);

            // 1. A plain turn streams text and ends with a result.
            await session.SendUserMessageAsync("Reply with exactly the word: pong");
            var first = await ReadTurnAsync(session);
            Assert.Contains("pong", first.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(first.Result.IsError);
            Assert.NotNull(first.Started);

            // 2. Interrupt mid-stream, then the same process must accept another turn.
            await session.SendUserMessageAsync("Count from 1 to 400, one number per line. No tools.");
            var interrupted = await ReadTurnAsync(session, onDelta: count => count == 3 ? session.InterruptAsync() : null);
            Assert.True(interrupted.Result.WasInterrupted);

            await session.SendUserMessageAsync("Reply with exactly the word: again");
            var afterInterrupt = await ReadTurnAsync(session);
            Assert.Contains("again", afterInterrupt.Text, StringComparison.OrdinalIgnoreCase);

            // 3. A write outside the folder asks Helm; allowing it runs the tool.
            await session.SendUserMessageAsync($"Use the Write tool to create {outside.Replace('\\', '/')} containing ok. Use no other tool.");
            var permitted = await ReadTurnAsync(session, onPermission: r => session.AllowAsync(r));
            Assert.Equal("Write", Assert.Single(permitted.Permissions).ToolName);
            Assert.Equal("ok", File.ReadAllText(outside).Trim());

            // 4. Denying is reported back to Claude as a failed tool result. A new file name, so Claude cannot
            //    decide the work is already done.
            await session.SendUserMessageAsync($"Now use the Write tool to create a different file, {secondOutside.Replace('\\', '/')}, containing no. Use no other tool. If it is refused, stop.");
            var denied = await ReadTurnAsync(session, onPermission: r => session.DenyAsync(r, "Denied by the Helm test."));
            Assert.True(denied.Permissions.Count > 0, "No permission request. Transcript: " + denied.Transcript);
            Assert.Contains(denied.ToolResults, t => t.IsError && t.Content.Contains("Denied by the Helm test."));
            Assert.False(File.Exists(secondOutside));

            // 5. "Allow for this chat" (a command rule forced to the session) covers the next matching command and
            //    writes nothing to the project's Claude Code settings.
            await session.SendUserMessageAsync("Use the Bash tool to run exactly this command and nothing else: mkdir alpha beta");
            var ruled = await ReadTurnAsync(session, onPermission: r =>
            {
                var rule = PermissionOptions.From(r.Suggestions).First(o => o.Label.StartsWith("Allow mkdir alpha", StringComparison.Ordinal));
                return session.AllowAsync(r, rule.UpdatedPermissions);
            });
            Assert.NotEmpty(ruled.Permissions);
            await session.SendUserMessageAsync("Use the Bash tool to run exactly this command and nothing else: mkdir alpha gamma");
            var covered = await ReadTurnAsync(session);
            Assert.True(covered.Permissions.Count == 0, "Asked again. Transcript: " + covered.Transcript);
            Assert.True(Directory.Exists(Path.Combine(folder, "gamma")));
            Assert.False(File.Exists(Path.Combine(folder, ".claude", "settings.local.json")));

            // 6. Switching the model mid-session applies to the next turn.
            await session.SetModelAsync("sonnet");
            await session.SendUserMessageAsync("Reply with exactly: switched");
            var switched = await ReadTurnAsync(session);
            Assert.Contains("sonnet", switched.Started?.Model ?? string.Empty);

            // 7. The CLI's saved copy of this conversation is found and read back (its file format is undocumented).
            var sessionId = first.Started!.SessionId;
            var saved = Assert.Single(ClaudeSessionStore.List(folder), s => s.SessionId == sessionId);
            Assert.False(string.IsNullOrWhiteSpace(saved.Title));
            var transcript = ClaudeSessionStore.ReadTranscript(saved.FilePath);
            Assert.Contains(transcript, e => e is TranscriptUserText { Text: "Reply with exactly the word: pong" });
            Assert.Contains(transcript, e => e is TranscriptToolUse { Name: "Write" });
        }
        finally
        {
            // The CLI keeps a history folder per working folder; do not leave one behind for every test run.
            if (ClaudeSessionStore.FindProjectDirectory(folder) is { } history)
            {
                try { Directory.Delete(history, recursive: true); } catch (IOException) { }
            }
            File.Delete(outside);
            File.Delete(secondOutside);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private sealed record Turn(SessionStarted? Started, string Text, TurnCompleted Result, IReadOnlyList<PermissionRequest> Permissions, IReadOnlyList<ToolResult> ToolResults, string Transcript);

    private static async Task<Turn> ReadTurnAsync(ClaudeSession session, Func<PermissionRequest, Task>? onPermission = null, Func<int, Task?>? onDelta = null)
    {
        using var timeout = new CancellationTokenSource(s_turnTimeout);
        SessionStarted? started = null;
        var text = new StringBuilder();
        var permissions = new List<PermissionRequest>();
        var toolResults = new List<ToolResult>();
        var deltas = 0;
        var transcript = new StringBuilder();
        await foreach (var ev in session.Events.ReadAllAsync(timeout.Token))
        {
            if (ev is not (TextDelta or ThinkingDelta or UnknownEvent))
            {
                var line = ev.ToString().ReplaceLineEndings(" ");
                transcript.Append(" | ").Append(line[..Math.Min(300, line.Length)]);
            }
            switch (ev)
            {
                case SessionStarted s:
                    started = s;
                    break;
                case TextDelta { ParentToolUseId: null } d:
                    text.Append(d.Text);
                    if (onDelta?.Invoke(++deltas) is { } pending) await pending;
                    break;
                case PermissionRequest r:
                    permissions.Add(r);
                    await (onPermission?.Invoke(r) ?? session.DenyAsync(r, "No handler in this test step."));
                    break;
                case ToolResult t:
                    toolResults.Add(t);
                    break;
                case TurnCompleted done:
                    return new Turn(started, text.ToString(), done, permissions, toolResults, transcript.ToString());
            }
        }
        throw new InvalidOperationException($"Claude Code exited mid-turn. stderr: {session.StandardErrorTail}");
    }
}
