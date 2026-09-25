using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Modules.ClaudeChat.Cli;

/// <summary>
/// The Claude Code CLI's stream-json protocol (<c>-p --input-format stream-json --output-format stream-json</c>):
/// one JSON object per line in both directions. Everything Helm knows about the wire format lives in this file, so a
/// CLI change is fixed in one place. Unknown lines become <see cref="UnknownEvent"/>; parsing never throws.
/// </summary>
public static class ClaudeProtocol
{
    /// <summary>Parses one stdout line. Returns several events for a message with several tool results, none for blank lines.</summary>
    public static IReadOnlyList<ClaudeEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return [];
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [new UnknownEvent("(not an object)", null)];
            var parent = Str(root, "parent_tool_use_id");
            var events = ParseRoot(root, Str(root, "type") ?? "(no type)");
            return parent is null ? events : events.Select(e => e with { ParentToolUseId = parent }).ToList();
        }
        catch (JsonException)
        {
            return [new UnknownEvent("(invalid json)", null)];
        }
    }

    // ---- stdin messages -------------------------------------------------------------------------------------------

    /// <summary>First line of every session; the CLI answers with a <see cref="ControlResponse"/> listing commands.</summary>
    public static string Initialize(string requestId) => ControlRequest(requestId, new JsonObject { ["subtype"] = "initialize" });

    /// <summary>Stops the running turn; the CLI then reports a <see cref="TurnCompleted"/> with <c>aborted_streaming</c>.</summary>
    public static string Interrupt(string requestId) => ControlRequest(requestId, new JsonObject { ["subtype"] = "interrupt" });

    public static string UserMessage(string text) => Serialize(new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
    });

    /// <summary>
    /// Lets the tool run with its original input. <paramref name="updatedPermissions"/> (entries taken from
    /// <see cref="PermissionRequest.Suggestions"/>) also makes the CLI stop asking for the same thing.
    /// </summary>
    public static string Allow(PermissionRequest request, JsonElement? updatedPermissions = null)
    {
        var decision = new JsonObject
        {
            ["behavior"] = "allow",
            ["updatedInput"] = JsonNode.Parse(request.Input.GetRawText()),
        };
        if (updatedPermissions is { } permissions) decision["updatedPermissions"] = JsonNode.Parse(permissions.GetRawText());
        return ControlResponse(request.RequestId, decision);
    }

    /// <summary>Refuses the tool call; Claude sees <paramref name="message"/> as the (failed) tool result.</summary>
    public static string Deny(PermissionRequest request, string message) =>
        ControlResponse(request.RequestId, new JsonObject { ["behavior"] = "deny", ["message"] = message });

    // ---- parsing --------------------------------------------------------------------------------------------------

    private static IReadOnlyList<ClaudeEvent> ParseRoot(JsonElement root, string type) => type switch
    {
        "system" => [ParseSystem(root)],
        "stream_event" => ParseStreamEvent(root),
        "assistant" => ParseAssistant(root),
        "user" => ParseUser(root),
        "result" => [ParseResult(root)],
        "control_request" => [ParseControlRequest(root)],
        "control_response" => [ParseControlResponse(root)],
        _ => [new UnknownEvent(type, Str(root, "subtype"))],
    };

    private static ClaudeEvent ParseSystem(JsonElement root) => Str(root, "subtype") switch
    {
        "init" => new SessionStarted(
            Str(root, "session_id") ?? string.Empty,
            Str(root, "model") ?? string.Empty,
            Str(root, "cwd") ?? string.Empty,
            Str(root, "permissionMode") ?? string.Empty,
            Str(root, "claude_code_version")),
        "permission_denied" => new PermissionDenied(
            Str(root, "tool_name") ?? string.Empty,
            Str(root, "tool_use_id"),
            Str(root, "message") ?? Str(root, "decision_reason") ?? "Permission denied."),
        var subtype => new UnknownEvent("system", subtype),
    };

    private static IReadOnlyList<ClaudeEvent> ParseStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var ev) || Str(ev, "type") != "content_block_delta"
            || !ev.TryGetProperty("delta", out var delta))
            return [];
        var index = Int(ev, "index");
        return Str(delta, "type") switch
        {
            "text_delta" => [new TextDelta(index, Str(delta, "text") ?? string.Empty)],
            "thinking_delta" => [new ThinkingDelta(index, Str(delta, "thinking") ?? string.Empty)],
            _ => [], // input_json_delta, signature_delta: the complete block arrives in the assistant message
        };
    }

    private static IReadOnlyList<ClaudeEvent> ParseAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)) return [];
        var blocks = new List<AssistantBlock>();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (Str(block, "type"))
                {
                    case "text":
                        blocks.Add(new AssistantText(Str(block, "text") ?? string.Empty));
                        break;
                    case "thinking":
                        blocks.Add(new AssistantThinking(Str(block, "thinking") ?? string.Empty));
                        break;
                    case "tool_use":
                        blocks.Add(new AssistantToolUse(
                            Str(block, "id") ?? string.Empty,
                            Str(block, "name") ?? string.Empty,
                            block.TryGetProperty("input", out var input) ? input.Clone() : EmptyObject()));
                        break;
                }
            }
        }
        return [new AssistantMessage(Str(message, "id") ?? string.Empty, blocks)];
    }

    private static IReadOnlyList<ClaudeEvent> ParseUser(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return [];
        if (content.ValueKind == JsonValueKind.String) return [new ConversationNotice(content.GetString() ?? string.Empty)];
        if (content.ValueKind != JsonValueKind.Array) return [];

        var events = new List<ClaudeEvent>();
        foreach (var block in content.EnumerateArray())
        {
            switch (Str(block, "type"))
            {
                case "tool_result":
                    events.Add(new ToolResult(
                        Str(block, "tool_use_id") ?? string.Empty,
                        block.TryGetProperty("content", out var c) ? FlattenText(c) : string.Empty,
                        Bool(block, "is_error")));
                    break;
                case "text":
                    events.Add(new ConversationNotice(Str(block, "text") ?? string.Empty));
                    break;
            }
        }
        return events;
    }

    private static ClaudeEvent ParseResult(JsonElement root) => new TurnCompleted(
        Str(root, "subtype") ?? string.Empty,
        Bool(root, "is_error"),
        Str(root, "terminal_reason"),
        root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDouble() : 0,
        root.TryGetProperty("duration_ms", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt64() : 0,
        Int(root, "num_turns"));

    private static ClaudeEvent ParseControlRequest(JsonElement root)
    {
        var id = Str(root, "request_id") ?? string.Empty;
        if (!root.TryGetProperty("request", out var request)) return new UnknownEvent("control_request", null);
        var subtype = Str(request, "subtype");
        if (subtype != "can_use_tool") return new UnknownEvent("control_request", subtype);

        var toolName = Str(request, "tool_name") ?? string.Empty;
        return new PermissionRequest(
            id,
            toolName,
            Str(request, "display_name") ?? toolName,
            request.TryGetProperty("input", out var input) ? input.Clone() : EmptyObject(),
            Str(request, "description"),
            Str(request, "decision_reason"),
            Str(request, "tool_use_id"),
            request.TryGetProperty("permission_suggestions", out var s) && s.ValueKind == JsonValueKind.Array ? s.Clone() : null);
    }

    private static ClaudeEvent ParseControlResponse(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response)) return new UnknownEvent("control_response", null);
        return new ControlResponse(
            Str(response, "request_id") ?? string.Empty,
            Str(response, "subtype") == "success",
            response.TryGetProperty("response", out var payload) ? payload.Clone() : null,
            Str(response, "error"));
    }

    /// <summary>Tool results are a string or an array of content blocks; keep the text parts.</summary>
    private static string FlattenText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;
        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            var text = Str(block, "type") switch
            {
                "text" => Str(block, "text"),
                "image" => "[image]",
                _ => null,
            };
            if (text is null) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static string ControlRequest(string requestId, JsonObject request) => Serialize(new JsonObject
    {
        ["type"] = "control_request",
        ["request_id"] = requestId,
        ["request"] = request,
    });

    private static string ControlResponse(string requestId, JsonObject response) => Serialize(new JsonObject
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject
        {
            ["subtype"] = "success",
            ["request_id"] = requestId,
            ["response"] = response,
        },
    });

    /// <summary>Compact, one line. Non-ASCII stays escaped (\uXXXX), so the stdin encoding can never mangle it.</summary>
    private static string Serialize(JsonNode node) => node.ToJsonString();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
