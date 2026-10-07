using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;

namespace Helm.Tests;

/// <summary>Helm's MCP consent policy: who is asked when, session allowances, time-outs, the queue, bursts and the activity log.</summary>
public sealed class McpConsentPolicyTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly FakePrompt _prompt = new();
    private readonly McpActivityLog _log = new(null);
    private readonly List<(string Title, string Message)> _notified = [];
    private bool _askBeforeChanges;
    private bool _helmElevated;

    private static readonly McpClientInfo Claude = new(Guid.NewGuid(), "claude-code", "2.0");

    private McpConsentPolicy Policy(TimeSpan? timeout = null) =>
        new(_prompt, _log, () => _askBeforeChanges, () => _helmElevated, _time, (t, m) => _notified.Add((t, m)))
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(2),
        };

    private static McpConsentRequest Request(McpRisk risk, string? target = null, bool elevated = false, McpDanger danger = McpDanger.Normal, string tool = "tool") =>
        new(tool, risk, $"Use {tool}", "Does a thing.", "Because.", "{\"a\":1}", target, elevated, danger);

    [Fact]
    public async Task Reads_are_never_asked_nor_logged()
    {
        var policy = Policy();
        Assert.Equal(McpConsentAnswer.AllowOnce, await policy.AskAsync(Request(McpRisk.Read), Claude, default));
        Assert.Empty(_prompt.Asked);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task Changes_run_without_a_question_while_Helm_is_not_elevated_and_are_logged()
    {
        var policy = Policy();
        var request = Request(McpRisk.Change);
        Assert.Equal(McpConsentAnswer.AllowOnce, await policy.AskAsync(request, Claude, default));
        Assert.Empty(_prompt.Asked);
        policy.Completed(request, Claude, McpConsentAnswer.AllowOnce, ok: true);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal("Allowed without asking", entry.Answer);
        Assert.Equal("ok", entry.Result);
        Assert.Equal("claude-code", entry.Client);
        Assert.Empty(_notified);
    }

    [Fact]
    public async Task Ask_before_every_change_asks_and_a_deny_is_logged()
    {
        _askBeforeChanges = true;
        var policy = Policy();
        _prompt.Answers.Enqueue(McpConsentAnswer.Deny);
        Assert.Equal(McpConsentAnswer.Deny, await policy.AskAsync(Request(McpRisk.Change), Claude, default));
        var asked = Assert.Single(_prompt.Asked);
        Assert.True(asked.OfferSession);
        Assert.False(asked.HelmElevated);
        Assert.Equal("Denied", Assert.Single(_log.Entries).Answer);
        Assert.Null(_log.Entries[0].Result);
    }

    [Fact]
    public async Task An_elevated_Helm_asks_every_change_says_who_is_not_elevated_and_offers_no_session()
    {
        _helmElevated = true;
        var policy = Policy();
        var caller = Claude with { Elevated = false };
        _prompt.Answers.Enqueue(McpConsentAnswer.AllowForSession);
        // The server's default question marks the call elevated while Helm is.
        var request = Request(McpRisk.Change, elevated: true);
        Assert.Equal(McpConsentAnswer.AllowOnce, await policy.AskAsync(request, caller, default));
        var asked = Assert.Single(_prompt.Asked);
        Assert.True(asked.HelmElevated);
        Assert.True(asked.CallerNotElevated);
        Assert.False(asked.OfferSession);
        Assert.Empty(policy.Allowances);

        // An elevated caller is not flagged.
        _prompt.Answers.Enqueue(McpConsentAnswer.AllowOnce);
        await policy.AskAsync(Request(McpRisk.Change, elevated: true), Claude with { Elevated = true }, default);
        Assert.False(_prompt.Asked[1].CallerNotElevated);
    }

    [Fact]
    public async Task Remote_calls_are_always_asked_and_each_allowed_one_is_notified()
    {
        var policy = Policy();
        _prompt.Answers.Enqueue(McpConsentAnswer.AllowOnce);
        var request = Request(McpRisk.Remote, target: "web");
        Assert.Equal(McpConsentAnswer.AllowOnce, await policy.AskAsync(request, Claude, default));
        Assert.Single(_prompt.Asked);
        policy.Completed(request, Claude, McpConsentAnswer.AllowOnce, ok: false);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(("Allowed once", "error", "web"), (entry.Answer, entry.Result, entry.Target));
        var (title, message) = Assert.Single(_notified);
        Assert.Contains("failed", title);
        Assert.Contains("Use tool", message);
    }

    [Fact]
    public async Task High_danger_and_elevated_calls_are_never_allowed_for_the_session()
    {
        var policy = Policy();
        foreach (var request in new[] { Request(McpRisk.Remote, "web", danger: McpDanger.High), Request(McpRisk.Remote, "web", elevated: true) })
        {
            _prompt.Answers.Enqueue(McpConsentAnswer.AllowForSession);
            Assert.Equal(McpConsentAnswer.AllowOnce, await policy.AskAsync(request, Claude, default));
            Assert.False(_prompt.Asked[^1].OfferSession);
        }
        Assert.Empty(policy.Allowances);
        _prompt.Answers.Enqueue(McpConsentAnswer.Deny);
        Assert.Equal(McpConsentAnswer.Deny, await policy.AskAsync(Request(McpRisk.Remote, "web", danger: McpDanger.High), Claude, default));
        Assert.Equal(3, _prompt.Asked.Count);
    }

    [Fact]
    public async Task A_session_allowance_covers_one_connection_tool_and_target_until_revoked_or_disconnected()
    {
        var policy = Policy();
        _prompt.Answers.Enqueue(McpConsentAnswer.AllowForSession);
        Assert.Equal(McpConsentAnswer.AllowForSession, await policy.AskAsync(Request(McpRisk.Remote, "web"), Claude, default));
        var allowance = Assert.Single(policy.Allowances);
        Assert.Equal(("tool", "web", "claude-code"), (allowance.Tool, allowance.Target, allowance.ClientName));

        // Same connection, tool and target: no question.
        Assert.Equal(McpConsentAnswer.AllowForSession, await policy.AskAsync(Request(McpRisk.Remote, "web"), Claude, default));
        Assert.Single(_prompt.Asked);

        // Another target, another tool, another connection: asked.
        _prompt.Answers.Enqueue(McpConsentAnswer.Deny);
        _prompt.Answers.Enqueue(McpConsentAnswer.Deny);
        _prompt.Answers.Enqueue(McpConsentAnswer.Deny);
        Assert.Equal(McpConsentAnswer.Deny, await policy.AskAsync(Request(McpRisk.Remote, "db"), Claude, default));
        Assert.Equal(McpConsentAnswer.Deny, await policy.AskAsync(Request(McpRisk.Remote, "web", tool: "other"), Claude, default));
        Assert.Equal(McpConsentAnswer.Deny, await policy.AskAsync(Request(McpRisk.Remote, "web"), Claude with { ConnectionId = Guid.NewGuid() }, default));
        Assert.Equal(4, _prompt.Asked.Count);

        var changes = 0;
        policy.AllowancesChanged += (_, _) => changes++;
        policy.KeepConnections([Claude.ConnectionId]);
        Assert.Single(policy.Allowances);
        policy.Revoke(policy.Allowances[0]);
        Assert.Empty(policy.Allowances);
        Assert.Equal(1, changes);
        _prompt.Answers.Enqueue(McpConsentAnswer.AllowForSession);
        await policy.AskAsync(Request(McpRisk.Remote, "web"), Claude, default);
        Assert.Equal(5, _prompt.Asked.Count);

        policy.KeepConnections([]); // the connection ended
        Assert.Empty(policy.Allowances);
    }

    [Fact]
    public async Task No_answer_in_time_is_a_deny()
    {
        var policy = Policy(TimeSpan.FromMilliseconds(100));
        _prompt.WaitForCancel = true;
        Assert.Equal(McpConsentAnswer.Deny, await policy.AskAsync(Request(McpRisk.Remote), Claude, default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Denied: no answer in time", Assert.Single(_log.Entries).Answer);
    }

    [Fact]
    public async Task A_client_that_leaves_cancels_its_question()
    {
        var policy = Policy();
        _prompt.WaitForCancel = true;
        using var gone = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.AskAsync(Request(McpRisk.Remote), Claude, gone.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Denied: the client left", Assert.Single(_log.Entries).Answer);
    }

    [Fact]
    public async Task Questions_come_one_at_a_time_and_a_queued_one_can_be_covered_by_the_first_answer()
    {
        var policy = Policy();
        _prompt.Gate = new TaskCompletionSource();
        _prompt.Answers.Enqueue(McpConsentAnswer.AllowForSession);
        var first = policy.AskAsync(Request(McpRisk.Remote, "web"), Claude, default);
        var second = policy.AskAsync(Request(McpRisk.Remote, "web"), Claude, default);
        var third = policy.AskAsync(Request(McpRisk.Remote, "db"), Claude, default);
        await Task.Delay(100);
        Assert.Single(_prompt.Asked); // the others wait in line

        _prompt.Answers.Enqueue(McpConsentAnswer.Deny);
        _prompt.Gate.SetResult();
        Assert.Equal(McpConsentAnswer.AllowForSession, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(McpConsentAnswer.AllowForSession, await second.WaitAsync(TimeSpan.FromSeconds(5))); // covered, not asked
        Assert.Equal(McpConsentAnswer.Deny, await third.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, _prompt.Asked.Count);
        Assert.Equal(1, _prompt.MostAtOnce);
    }

    [Fact]
    public async Task A_burst_of_questions_is_refused_with_a_message_Claude_can_read()
    {
        var policy = Policy();
        for (var i = 0; i < McpConsentPolicy.MaxAskedPerMinute; i++)
        {
            _prompt.Answers.Enqueue(McpConsentAnswer.AllowOnce);
            await policy.AskAsync(Request(McpRisk.Remote, $"s{i}"), Claude, default);
        }
        var refused = await Assert.ThrowsAsync<McpToolException>(() => policy.AskAsync(Request(McpRisk.Remote, "one more"), Claude, default));
        Assert.Contains("did not ask the user", refused.Message);
        Assert.Equal(McpConsentPolicy.MaxAskedPerMinute, _prompt.Asked.Count);
        Assert.Equal("Refused: too many questions", _log.Entries[0].Answer);

        // Calls that need no question are not held back.
        Assert.Equal(McpConsentAnswer.AllowOnce, await policy.AskAsync(Request(McpRisk.Change), Claude, default));

        _time.Advance(TimeSpan.FromMinutes(1));
        _prompt.Answers.Enqueue(McpConsentAnswer.AllowOnce);
        Assert.Equal(McpConsentAnswer.AllowOnce, await policy.AskAsync(Request(McpRisk.Remote, "later"), Claude, default));
    }

    [Fact]
    public async Task Through_the_server_a_deny_reaches_Claude_and_an_allowed_call_runs_and_is_logged()
    {
        _askBeforeChanges = true;
        var policy = Policy();
        var runs = 0;
        var tool = new McpTool("notes_edit", "Edits a note.", McpArgs.Schema(("text", McpArgs.Text("New text."), true)), (_, _) =>
        {
            runs++;
            return Task.FromResult<object?>("done");
        });
        var server = new McpServer([tool], "0.29.0", "test", consent: policy);

        _prompt.Answers.Enqueue(McpConsentAnswer.Deny);
        var denied = await Call(server, "notes_edit", new { text = "hello" });
        Assert.True(denied.IsError);
        Assert.Equal("The user declined: Use “notes_edit”.", denied.Text);
        Assert.Equal(0, runs);
        Assert.Equal("{\"text\":\"hello\"}", _prompt.Asked[0].Request.Details);

        _prompt.Answers.Enqueue(McpConsentAnswer.AllowOnce);
        var allowed = await Call(server, "notes_edit", new { text = "hello" });
        Assert.False(allowed.IsError);
        Assert.Equal(1, runs);
        Assert.Equal(("Allowed once", "ok"), (_log.Entries[0].Answer, _log.Entries[0].Result));
        Assert.Equal(("Denied", (string?)null), (_log.Entries[1].Answer, _log.Entries[1].Result));
    }

    [Fact]
    public async Task The_question_hides_secret_arguments_so_the_log_never_keeps_them()
    {
        _askBeforeChanges = true;
        var policy = Policy();
        var schema = McpArgs.Schema(("name", McpArgs.Text("Name."), true), ("pin", McpArgs.Text("PIN."), true), ("apiToken", McpArgs.Text("Token."), false));
        schema["properties"]!["pin"]!["writeOnly"] = true;
        var tool = new McpTool("vault_like", "Stores a thing.", schema, (_, _) => Task.FromResult<object?>("ok"));
        var server = new McpServer([tool], "0.29.0", "test", consent: policy);
        _prompt.Answers.Enqueue(McpConsentAnswer.AllowOnce);
        await Call(server, "vault_like", new { name = "bank", pin = "1234", apiToken = "abcd" });

        var details = _prompt.Asked[0].Request.Details!;
        Assert.Contains("bank", details);
        Assert.DoesNotContain("1234", details);
        Assert.DoesNotContain("abcd", details);
        Assert.DoesNotContain("1234", _log.Entries[0].Details);
    }

    [Fact]
    public void The_activity_log_keeps_the_last_500_on_disk_and_clears()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "mcp", "activity.json");
        var log = new McpActivityLog(file);
        for (var i = 0; i < McpActivityLog.MaxEntries + 20; i++)
            log.Add(new McpActivityEntry(_time.GetLocalNow(), "claude", $"t{i}", McpRisk.Change, "Title", null, new string('x', 2000), "Allowed", "ok"));
        var reloaded = new McpActivityLog(file).Entries;
        Assert.Equal(McpActivityLog.MaxEntries, reloaded.Count);
        Assert.Equal($"t{McpActivityLog.MaxEntries + 19}", reloaded[0].Tool); // newest first
        Assert.Equal("t20", reloaded[^1].Tool);
        Assert.Equal(McpActivityLog.MaxDetails + 1, reloaded[0].Details!.Length);
        Assert.Equal(McpRisk.Change, reloaded[0].Risk);

        log.Clear();
        Assert.Empty(log.Entries);
        Assert.Empty(new McpActivityLog(file).Entries);
    }

    [Fact]
    public void A_damaged_log_starts_again()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "activity.json");
        File.WriteAllText(file, "{ not json");
        var log = new McpActivityLog(file);
        Assert.Empty(log.Entries);
        log.Add(new McpActivityEntry(_time.GetLocalNow(), "claude", "t", McpRisk.Remote, "Title", "web", null, "Denied", null));
        Assert.Single(new McpActivityLog(file).Entries);
    }

    private static async Task<(bool IsError, string Text)> Call(McpServer server, string tool, object args)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(JsonSerializer.Serialize(args)) },
        };
        var reply = await server.HandleAsync(request.ToJsonString(), default);
        var result = reply!["result"]!;
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }

    /// <summary>The dialog: answers from a queue, or waits (for a gate, or until cancelled).</summary>
    private sealed class FakePrompt : IMcpConsentPrompt
    {
        private int _open;

        public List<McpConsentPrompt> Asked { get; } = [];

        public Queue<McpConsentAnswer> Answers { get; } = new();

        public bool WaitForCancel { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public int MostAtOnce { get; private set; }

        public async Task<McpConsentAnswer> AskAsync(McpConsentPrompt prompt, CancellationToken ct)
        {
            MostAtOnce = Math.Max(MostAtOnce, Interlocked.Increment(ref _open));
            try
            {
                lock (Asked) Asked.Add(prompt);
                if (WaitForCancel) await Task.Delay(Timeout.Infinite, ct);
                if (Gate is { } gate) await gate.Task.WaitAsync(ct);
                lock (Answers) return Answers.Dequeue();
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        }
    }
}
