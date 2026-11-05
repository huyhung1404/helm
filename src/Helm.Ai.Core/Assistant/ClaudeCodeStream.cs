using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Ai.Assistant;

/// <summary>
/// One turn of Claude Code's stream-json output (<c>--output-format stream-json --verbose --include-partial-messages</c>):
/// the API's stream events of each assistant message (<c>stream_event</c>: message_start, then content_block_delta with
/// text_delta pieces) and the turn's <c>result</c> line, which carries the whole answer and its tokens.
/// </summary>
public static class ClaudeCodeStream
{
    /// <summary>The turn's result line, as Claude Code wrote it.</summary>
    public sealed record Result(string Answer, AssistantUsage Usage, bool IsError);

    /// <summary>
    /// Reads up to the turn's result, telling <paramref name="soFar"/> the text of the message being written after each
    /// piece (a new message starts it again). Null when the output ended first (the process died or was stopped).
    /// </summary>
    public static async Task<Result?> ReadTurnAsync(Func<CancellationToken, Task<string?>> readLine, Action<string>? soFar, CancellationToken ct)
    {
        var text = new StringBuilder();
        while (await readLine(ct) is { } line)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonNode? ev;
            try
            {
                ev = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            switch (Text(ev?["type"]))
            {
                case "stream_event" when soFar is not null:
                    // A sub-agent's message (parent_tool_use_id set) is not the answer; Helm's agent has none anyway.
                    if (Text(ev!["parent_tool_use_id"]) is not null) break;
                    var inner = ev["event"];
                    switch (Text(inner?["type"]))
                    {
                        case "message_start":
                            text.Clear();
                            break;
                        case "content_block_delta" when Text(inner!["delta"]?["type"]) == "text_delta":
                            text.Append(Text(inner["delta"]!["text"]));
                            soFar(text.ToString());
                            break;
                    }
                    break;
                case "result":
                    var usage = ev!["usage"];
                    long Read(string name) => usage?[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
                    return new Result(Text(ev["result"]) ?? "",
                        new AssistantUsage(Read("input_tokens"), Read("output_tokens"), Read("cache_read_input_tokens"), Read("cache_creation_input_tokens")),
                        ev["is_error"] is JsonValue e && e.TryGetValue<bool>(out var failed) && failed);
            }
        }
        return null;
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
