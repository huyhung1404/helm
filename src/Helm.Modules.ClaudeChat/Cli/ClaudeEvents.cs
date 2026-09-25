using System.Text.Json;

namespace Helm.Modules.ClaudeChat.Cli;

/// <summary>
/// Something the Claude Code CLI reported on stdout, already parsed. <see cref="ParentToolUseId"/> is set when the
/// event belongs to a subagent (the Task tool call that spawned it), null for the main conversation.
/// </summary>
public abstract record ClaudeEvent
{
    public string? ParentToolUseId { get; init; }
}

/// <summary><c>system/init</c>: sent at the start of every turn with the session's current configuration.</summary>
public sealed record SessionStarted(string SessionId, string Model, string WorkingDirectory, string PermissionMode, string? CliVersion) : ClaudeEvent;

/// <summary>A streamed piece of assistant text (<c>stream_event</c> / <c>text_delta</c>).</summary>
public sealed record TextDelta(int BlockIndex, string Text) : ClaudeEvent;

/// <summary>A streamed piece of a thinking summary (<c>thinking_delta</c>); often empty when thinking is not displayed.</summary>
public sealed record ThinkingDelta(int BlockIndex, string Text) : ClaudeEvent;

/// <summary>A complete assistant message (<c>assistant</c>). Text in it was already streamed as <see cref="TextDelta"/>s.</summary>
public sealed record AssistantMessage(string MessageId, IReadOnlyList<AssistantBlock> Blocks) : ClaudeEvent;

public abstract record AssistantBlock;

public sealed record AssistantText(string Text) : AssistantBlock;

public sealed record AssistantThinking(string Text) : AssistantBlock;

/// <summary>A tool call. <see cref="Input"/> is a detached copy, safe to keep.</summary>
public sealed record AssistantToolUse(string Id, string Name, JsonElement Input) : AssistantBlock;

/// <summary>The outcome of a tool call (a <c>tool_result</c> block inside a <c>user</c> message).</summary>
public sealed record ToolResult(string ToolUseId, string Content, bool IsError) : ClaudeEvent;

/// <summary>A plain text line the CLI inserts into the conversation, e.g. "[Request interrupted by user]".</summary>
public sealed record ConversationNotice(string Text) : ClaudeEvent;

/// <summary>
/// <c>control_request</c> / <c>can_use_tool</c>: the CLI waits until the host answers with
/// <see cref="ClaudeProtocol.Allow"/> or <see cref="ClaudeProtocol.Deny"/>.
/// </summary>
public sealed record PermissionRequest(
    string RequestId,
    string ToolName,
    string DisplayName,
    JsonElement Input,
    string? Description,
    string? DecisionReason,
    string? ToolUseId,
    JsonElement? Suggestions) : ClaudeEvent;

/// <summary><c>system/permission_denied</c>: a tool call was refused without asking (rules, mode, or no host answer).</summary>
public sealed record PermissionDenied(string ToolName, string? ToolUseId, string Message) : ClaudeEvent;

/// <summary><c>result</c>: the turn is over and the CLI is idle, waiting for the next user message.</summary>
public sealed record TurnCompleted(string Subtype, bool IsError, string? TerminalReason, double CostUsd, long DurationMs, int NumTurns) : ClaudeEvent
{
    public bool WasInterrupted => TerminalReason == "aborted_streaming";
}

/// <summary>A reply to a <c>control_request</c> Helm sent (initialize, interrupt). Consumed by <see cref="ClaudeSession"/>.</summary>
public sealed record ControlResponse(string RequestId, bool Success, JsonElement? Response, string? Error) : ClaudeEvent;

/// <summary>Any line Helm does not model (rate limits, status, thinking token estimates, future event types).</summary>
public sealed record UnknownEvent(string Type, string? Subtype) : ClaudeEvent;
