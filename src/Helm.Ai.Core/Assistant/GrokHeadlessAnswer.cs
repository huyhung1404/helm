using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Ai.Assistant;

/// <summary>What a headless <c>grok --output-format json</c> run printed.</summary>
public static class GrokHeadlessAnswer
{
    /// <summary>
    /// The answer in Grok's one JSON object (<c>{"text", "stopReason", "sessionId", "usage": {input_tokens,
    /// cache_read_input_tokens, output_tokens, reasoning_tokens, total_tokens}…}</c>), or its plain text, or the error it reports.
    /// </summary>
    public static (string? Answer, string? Error, AssistantUsage? Usage) Read(string output)
    {
        var text = output.Trim();
        var start = text.IndexOf('{');
        if (start < 0) return (text.Length > 0 ? text : null, null, null);
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(text[start..]);
        }
        catch (JsonException)
        {
            return (text, null, null);
        }
        if (json?["is_error"] is JsonValue failed && failed.TryGetValue<bool>(out var isError) && isError
            || json?["error"] is JsonNode)
            return (null, Text(json!["error"]) ?? Text(json["text"]) ?? "it stopped with an error", null);
        AssistantUsage? usage = null;
        if (json?["usage"] is JsonObject u)
        {
            long Count(string name) => u[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
            // input_tokens leaves the cached part out (Grok 1.0.70: input + cached + output = total_tokens).
            usage = new AssistantUsage(Count("input_tokens"), Count("output_tokens"), Count("cache_read_input_tokens"), Count("cache_creation_input_tokens"));
        }
        foreach (var name in new[] { "text", "result", "response", "content", "message", "output" })
            if (Text(json?[name]) is { } answer) return (answer, null, usage);
        return (null, "Helm could not read Grok's answer.", null);

        static string? Text(JsonNode? node) => node switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonObject o => Text(o["message"]) ?? Text(o["text"]) ?? Text(o["content"]),
            JsonArray a => string.Concat(a.Select(Text)) is { Length: > 0 } joined ? joined : null,
            _ => null,
        };
    }
}
