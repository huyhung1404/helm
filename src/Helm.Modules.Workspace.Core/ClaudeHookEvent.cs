using System.Text.Json;

namespace Helm.Modules.Workspace;

/// <summary>
/// One Claude Code hook call, as its JSON arrives (an <c>http</c> hook POSTs the hook input). Only the fields Helm uses;
/// unknown events and fields are ignored, so a newer Claude Code never breaks it.
/// </summary>
public sealed record ClaudeHookEvent
{
    public const string SessionStart = "SessionStart";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string Stop = "Stop";
    public const string Notification = "Notification";
    public const string PermissionRequest = "PermissionRequest";
    public const string PreToolUse = "PreToolUse";
    public const string PostToolUse = "PostToolUse";
    public const string SessionEnd = "SessionEnd";

    /// <summary>The events Helm subscribes to, in the order they are written into Claude's settings.</summary>
    public static IReadOnlyList<string> Subscribed { get; } =
        [SessionStart, UserPromptSubmit, Stop, Notification, PermissionRequest, PreToolUse, PostToolUse, SessionEnd];

    public required string Event { get; init; }
    public required string SessionId { get; init; }
    public string Cwd { get; init; } = "";
    public string TranscriptPath { get; init; } = "";

    /// <summary>UserPromptSubmit: the text the session received.</summary>
    public string? Prompt { get; init; }

    /// <summary>Stop: Claude's last answer of the turn.</summary>
    public string? LastAssistantMessage { get; init; }

    /// <summary>PreToolUse / PermissionRequest: the tool.</summary>
    public string? ToolName { get; init; }

    /// <summary>PreToolUse / PermissionRequest: the tool's input as JSON (the command, the file, the questions).</summary>
    public string? ToolInputJson { get; init; }

    /// <summary>Notification: the text Claude Code shows.</summary>
    public string? Message { get; init; }

    public string? NotificationType { get; init; }

    /// <summary>SessionStart: startup, resume, clear or compact. SessionEnd: why it ended.</summary>
    public string? Source { get; init; }

    public string? PermissionMode { get; init; }

    /// <summary>Stop: true when a Stop hook already made Claude continue (the turn is not over).</summary>
    public bool StopHookActive { get; init; }

    /// <summary>Null when the JSON is not a hook event Helm can use (no event name or session).</summary>
    public static ClaudeHookEvent? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var name = Text(root, "hook_event_name");
            var session = Text(root, "session_id");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(session)) return null;
            return new ClaudeHookEvent
            {
                Event = name,
                SessionId = session,
                Cwd = Text(root, "cwd") ?? "",
                TranscriptPath = Text(root, "transcript_path") ?? "",
                Prompt = Text(root, "prompt"),
                LastAssistantMessage = Text(root, "last_assistant_message"),
                ToolName = Text(root, "tool_name"),
                ToolInputJson = root.TryGetProperty("tool_input", out var toolInput) && toolInput.ValueKind == JsonValueKind.Object ? toolInput.GetRawText() : null,
                Message = Text(root, "message"),
                NotificationType = Text(root, "notification_type"),
                Source = Text(root, "source") ?? Text(root, "reason"),
                PermissionMode = Text(root, "permission_mode"),
                StopHookActive = root.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
