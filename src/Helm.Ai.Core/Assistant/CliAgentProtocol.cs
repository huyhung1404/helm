using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Ai.Assistant;

/// <summary>
/// How an agent CLI that has no tool calling of its own for Helm (Claude Code with its tools off; later Codex, Gemini
/// CLI) is offered Helm's tools: they are listed in the first message, it answers with the calls it wants as one JSON
/// object, and gets the results in the next message.
/// </summary>
public static class CliAgentProtocol
{
    /// <summary>The first message's preamble: the assistant's instructions, the tools, and how to call them.</summary>
    public static string Preamble(string system, IReadOnlyList<AssistantTool> tools)
    {
        // A text-only job (a VPS service's, a tool's text call) offers no tool, so it is not told how to call one; the
        // answer keeps the form its instructions ask for (a tool's text call wants JSON).
        if (tools.Count == 0) return system.Trim() + "\n\nYour own tools are turned off. Answer with text only, in the form asked for above.";
        var text = new StringBuilder(system.Trim()).Append("\n\n")
            .Append("Helm runs these tools for you; your own are off. Each: name: what it does, then its input (? optional, (hint)):\n");
        List(text, tools);
        text.Append("""

            To call tools, reply with ONLY one JSON object, e.g.
            {"calls":[{"name":"wallet_add_transaction","input":{"amount":"45k","direction":"spend","description":"Phở","summary":"Chi 45.000đ · Phở"}}]}
            One reply may hold several calls; Helm answers with their results. When done, answer the user in plain text (never JSON).
            """);
        return text.ToString();
    }

    /// <summary>
    /// Tools a later turn adds to the conversation, put before that turn's text, with how to call them (a first message
    /// that offered no tool did not say).
    /// </summary>
    public static string MoreTools(IReadOnlyList<AssistantTool> tools) =>
        List(new StringBuilder("More tools you may call from now on:\n"), tools)
            .Append("""To call tools, reply with ONLY one JSON object: {"calls":[{"name":"…","input":{…}}]}""").Append('\n').ToString();

    private static StringBuilder List(StringBuilder text, IReadOnlyList<AssistantTool> tools)
    {
        foreach (var tool in tools)
            text.Append("- ").Append(tool.Name).Append(": ").Append(tool.Description).Append("\n  ").Append(Signature(tool.Schema)).Append('\n');
        return text;
    }

    /// <summary>
    /// A schema written the short way a model reads as well as JSON Schema, at half the tokens:
    /// <c>{amount: string (đồng: 45k), direction: "spend"|"income", when?: string}</c>.
    /// </summary>
    internal static string Signature(JsonNode? schema)
    {
        if (schema is not JsonObject node) return "any";
        if (node["enum"] is JsonArray values) return string.Join("|", values.Select(v => v?.ToJsonString() ?? "null"));
        if (node["anyOf"] is JsonArray options) return string.Join(" | ", options.Select(Signature));
        var type = Text(node["type"]);
        if (type == "array")
        {
            var item = Signature(node["items"]);
            return item.Contains(" | ", StringComparison.Ordinal) ? $"({item})[]" : item + "[]";
        }
        if (type != "object" && node["properties"] is null) return type switch { "integer" => "number", null => "any", _ => type };
        if (node["properties"] is not JsonObject properties || properties.Count == 0) return "{}";
        var required = node["required"] is JsonArray list ? list.Select(Text).ToHashSet() : [];
        return "{" + string.Join(", ", properties.Select(p =>
        {
            var hint = Text(p.Value?["description"]) is { Length: > 0 } d ? $" ({d.TrimEnd('.')})" : "";
            return $"{p.Key}{(required.Contains(p.Key) ? "" : "?")}: {Signature(p.Value)}{hint}";
        })) + "}";
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The message that carries the results of the calls of the last answer: one line each, as the tool gave it.</summary>
    public static string Results(IReadOnlyList<ToolOutcome> results) =>
        "Tool results:\n" + string.Join("\n", results.Select(r => $"{r.CallId}{(r.IsError ? " (error)" : "")}: {r.Content}"));

    /// <summary>
    /// The calls an answer asks for (one JSON object with <c>calls</c>, bare or in a code fence), numbered after
    /// <paramref name="callsSoFar"/>; or, for anything else, the answer itself for the user.
    /// </summary>
    public static AssistantStep Parse(string answer, AssistantUsage usage, ref int callsSoFar)
    {
        var text = answer.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text.Trim('`').Trim();
            if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase)) text = text[4..].Trim();
        }
        if (text.StartsWith('{') && text.EndsWith('}'))
        {
            try
            {
                if (JsonNode.Parse(text)?["calls"] is JsonArray list)
                {
                    var calls = new List<ToolCall>();
                    foreach (var call in list.OfType<JsonObject>())
                    {
                        calls.Add(new ToolCall($"c{++callsSoFar}", call["name"]?.GetValue<string>() ?? "",
                            JsonDocument.Parse(call["input"]?.ToJsonString() ?? "{}").RootElement.Clone()));
                    }
                    return new AssistantStep("", calls, usage, false);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Not a call list after all: the user gets it as said.
            }
        }
        return new AssistantStep(answer.Trim(), [], usage, false);
    }
}
