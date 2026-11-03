using System.Text;
using System.Text.Json.Nodes;

namespace Helm.Ai.Assistant;

/// <summary>
/// Reads xAI's streamed chat completion (server-sent events, one <c>data:</c> chunk a line, <c>[DONE]</c> last): tells
/// the text so far as it comes, gathers the tool calls piece by piece, and gives back the same shape a plain call
/// answers (<c>choices[0].message</c> and <c>usage</c>), so the chat reads both alike.
/// </summary>
internal static class GrokStream
{
    public static async Task<JsonNode> ReadAsync(HttpResponseMessage response, Action<string> partial, CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, Call>();
        JsonNode? usage = null;
        try
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                if (data.Length == 0 || data[0] != '{') continue;
                var chunk = JsonNode.Parse(data);
                if (chunk?["error"] is { } error) throw new AssistantException($"xAI answered: {error["message"]?.ToString() ?? error.ToJsonString()}");
                if (chunk?["usage"] is JsonObject counted) usage = counted.DeepClone();
                if (chunk?["choices"]?[0]?["delta"] is not JsonObject delta) continue;
                if (delta["content"] is JsonValue content && content.TryGetValue<string>(out var piece) && piece.Length > 0)
                {
                    text.Append(piece);
                    partial(text.ToString());
                }
                if (delta["tool_calls"] is not JsonArray pieces) continue;
                // Text before tool calls is not the answer: the card takes it away again.
                if (calls.Count == 0 && text.Length > 0) partial("");
                foreach (var each in pieces.OfType<JsonObject>())
                {
                    var index = each["index"] is JsonValue i && i.TryGetValue<int>(out var n) ? n : calls.Count;
                    if (!calls.TryGetValue(index, out var call)) calls[index] = call = new Call();
                    if (each["id"]?.GetValue<string>() is { Length: > 0 } id) call.Id = id;
                    if (each["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name) call.Name = name;
                    call.Arguments.Append(each["function"]?["arguments"]?.GetValue<string>());
                }
            }
        }
        catch (IOException ex)
        {
            throw new AssistantException("xAI stopped answering midway. Try again.", ex);
        }
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
        if (calls.Count > 0)
        {
            message["tool_calls"] = new JsonArray([.. calls.Values.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.ToString() },
            })]);
        }
        return new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }), ["usage"] = usage };
    }

    private sealed class Call
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
    }
}
