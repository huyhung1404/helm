using System.Text.Json;
using Helm.Modules.ClaudeChat.Cli;

namespace Helm.Tests;

public class ClaudeProtocolTests
{
    // Recorded from Claude Code 2.1.281 (paths and ids scrubbed): a text-only turn, a Write that asks for permission,
    // and a Write refused without asking.
    private static readonly IReadOnlyList<ClaudeEvent> s_recorded = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-stream.jsonl"))
        .SelectMany(ClaudeProtocol.Parse)
        .ToList();

    [Fact]
    public void Recorded_stream_parses_without_invalid_lines()
    {
        Assert.NotEmpty(s_recorded);
        Assert.DoesNotContain(s_recorded, e => e is UnknownEvent { Type: "(invalid json)" or "(no type)" });
    }

    [Fact]
    public void Init_reports_session_configuration()
    {
        var init = s_recorded.OfType<SessionStarted>().First();

        Assert.Equal("00000000-0000-4000-8000-000000000001", init.SessionId);
        Assert.Equal("claude-haiku-4-5-20251001", init.Model);
        Assert.Equal(@"C:\work", init.WorkingDirectory);
        Assert.Equal("2.1.281", init.CliVersion);
    }

    [Fact]
    public void Streamed_text_matches_the_final_assistant_message()
    {
        var firstTurn = s_recorded.TakeWhile(e => e is not TurnCompleted).ToList();

        var streamed = string.Concat(firstTurn.OfType<TextDelta>().Select(d => d.Text));
        var final = string.Concat(firstTurn.OfType<AssistantMessage>().SelectMany(m => m.Blocks).OfType<AssistantText>().Select(t => t.Text));

        Assert.False(string.IsNullOrEmpty(streamed));
        Assert.Equal(final, streamed);
    }

    [Fact]
    public void Turn_result_carries_cost_and_outcome()
    {
        var result = s_recorded.OfType<TurnCompleted>().First();

        Assert.Equal("success", result.Subtype);
        Assert.False(result.IsError);
        Assert.False(result.WasInterrupted);
        Assert.True(result.CostUsd > 0);
    }

    [Fact]
    public void Permission_request_exposes_tool_and_input()
    {
        var request = Assert.Single(s_recorded.OfType<PermissionRequest>());

        Assert.Equal("Write", request.ToolName);
        Assert.False(string.IsNullOrEmpty(request.RequestId));
        Assert.Equal(@"D:\helm_probe_outside.txt", request.Input.GetProperty("file_path").GetString());
        Assert.Equal("Path is outside allowed working directories", request.DecisionReason);
        Assert.NotNull(request.Suggestions);
        Assert.Contains(s_recorded.OfType<AssistantMessage>().SelectMany(m => m.Blocks).OfType<AssistantToolUse>(), t => t.Id == request.ToolUseId);
    }

    [Fact]
    public void Denied_tool_call_is_reported()
    {
        var denied = Assert.Single(s_recorded.OfType<PermissionDenied>());

        Assert.Equal("Write", denied.ToolName);
        Assert.Contains("haven't granted", denied.Message);
    }

    [Fact]
    public void Tool_results_are_flattened_to_text()
    {
        var result = s_recorded.OfType<ToolResult>().First();

        Assert.True(result.IsError);
        Assert.False(string.IsNullOrEmpty(result.Content));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_lines_produce_nothing(string line) => Assert.Empty(ClaudeProtocol.Parse(line));

    [Theory]
    [InlineData("not json", "(invalid json)")]
    [InlineData("[1,2]", "(not an object)")]
    [InlineData("{\"type\":\"something_new\",\"subtype\":\"x\"}", "something_new")]
    [InlineData("{\"type\":\"control_request\",\"request_id\":\"1\",\"request\":{\"subtype\":\"hook_callback\"}}", "control_request")]
    public void Unexpected_lines_become_unknown_events(string line, string type)
    {
        var ev = Assert.IsType<UnknownEvent>(Assert.Single(ClaudeProtocol.Parse(line)));
        Assert.Equal(type, ev.Type);
    }

    [Fact]
    public void Subagent_events_keep_their_parent_tool_use()
    {
        const string line = """{"type":"stream_event","parent_tool_use_id":"toolu_parent","event":{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"hi"}}}""";

        var delta = Assert.IsType<TextDelta>(Assert.Single(ClaudeProtocol.Parse(line)));

        Assert.Equal("toolu_parent", delta.ParentToolUseId);
        Assert.Equal(2, delta.BlockIndex);
        Assert.Equal("hi", delta.Text);
    }

    [Fact]
    public void Interrupted_turn_is_recognised()
    {
        var notice = Assert.IsType<ConversationNotice>(Assert.Single(ClaudeProtocol.Parse(
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""")));
        var result = Assert.IsType<TurnCompleted>(Assert.Single(ClaudeProtocol.Parse(
            """{"type":"result","subtype":"error_during_execution","is_error":true,"terminal_reason":"aborted_streaming","num_turns":1}""")));

        Assert.Equal("[Request interrupted by user]", notice.Text);
        Assert.True(result.WasInterrupted);
    }

    [Theory]
    [InlineData("aborted_tools", true)]
    [InlineData("aborted_streaming", true)]
    [InlineData("completed", false)]
    [InlineData(null, false)]
    public void Any_aborted_terminal_reason_counts_as_interrupted(string? reason, bool interrupted) =>
        Assert.Equal(interrupted, new TurnCompleted("error_during_execution", true, reason, 0, 0, 1).WasInterrupted);

    [Fact]
    public void User_message_is_one_ascii_line()
    {
        var line = ClaudeProtocol.UserMessage("Xin chào Lumi 💗\nline two");

        Assert.DoesNotContain('\n', line);
        Assert.True(line.All(c => c < 128));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("user", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("Xin chào Lumi 💗\nline two", doc.RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void Allow_echoes_the_original_input()
    {
        var request = s_recorded.OfType<PermissionRequest>().Single();

        using var doc = JsonDocument.Parse(ClaudeProtocol.Allow(request));
        var response = doc.RootElement.GetProperty("response");

        Assert.Equal("control_response", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("success", response.GetProperty("subtype").GetString());
        Assert.Equal(request.RequestId, response.GetProperty("request_id").GetString());
        var decision = response.GetProperty("response");
        Assert.Equal("allow", decision.GetProperty("behavior").GetString());
        Assert.Equal(request.Input.GetRawText(), decision.GetProperty("updatedInput").GetRawText());
        Assert.False(decision.TryGetProperty("updatedPermissions", out _));
    }

    [Fact]
    public void Allow_can_carry_updated_permissions()
    {
        var request = s_recorded.OfType<PermissionRequest>().Single();

        using var doc = JsonDocument.Parse(ClaudeProtocol.Allow(request, request.Suggestions));

        var permissions = doc.RootElement.GetProperty("response").GetProperty("response").GetProperty("updatedPermissions");
        Assert.Equal(request.Suggestions!.Value.GetArrayLength(), permissions.GetArrayLength());
    }

    [Fact]
    public void Deny_sends_the_reason()
    {
        var request = s_recorded.OfType<PermissionRequest>().Single();

        using var doc = JsonDocument.Parse(ClaudeProtocol.Deny(request, "Not now"));

        var decision = doc.RootElement.GetProperty("response").GetProperty("response");
        Assert.Equal("deny", decision.GetProperty("behavior").GetString());
        Assert.Equal("Not now", decision.GetProperty("message").GetString());
    }

    [Fact]
    public void Control_requests_carry_their_id()
    {
        using var init = JsonDocument.Parse(ClaudeProtocol.Initialize("helm-1"));
        using var interrupt = JsonDocument.Parse(ClaudeProtocol.Interrupt("helm-2"));

        Assert.Equal("helm-1", init.RootElement.GetProperty("request_id").GetString());
        Assert.Equal("initialize", init.RootElement.GetProperty("request").GetProperty("subtype").GetString());
        Assert.Equal("interrupt", interrupt.RootElement.GetProperty("request").GetProperty("subtype").GetString());
    }

    [Fact]
    public void Control_responses_are_parsed_for_correlation()
    {
        var ok = Assert.IsType<ControlResponse>(Assert.Single(ClaudeProtocol.Parse(
            """{"type":"control_response","response":{"subtype":"success","request_id":"helm-2","response":{"still_queued":[]}}}""")));
        var failed = Assert.IsType<ControlResponse>(Assert.Single(ClaudeProtocol.Parse(
            """{"type":"control_response","response":{"subtype":"error","request_id":"helm-3","error":"boom"}}""")));

        Assert.True(ok.Success);
        Assert.Equal("helm-2", ok.RequestId);
        Assert.False(failed.Success);
        Assert.Equal("boom", failed.Error);
    }
}
