using System.Text;
using System.Text.Json.Nodes;

namespace Helm.Ai.Assistant;

/// <summary>
/// One conversation with a running <c>grok agent stdio</c> (the Agent Client Protocol, Grok 1.0.70: JSON-RPC 2.0, one
/// JSON object a line on stdin/stdout). Its session stays loaded between turns, so a turn costs no process start, no
/// sign-in check and no session reload as a headless <c>grok --prompt-file</c> run does. The process, model and agent
/// profile are set up by whoever started it; this speaks the protocol, tells Grok this client can neither read files
/// nor run commands, turns down every permission Grok asks for, and refuses a session that offers tools of its own.
/// </summary>
public sealed class GrokAcp(TextReader output, TextWriter input)
{
    private long _id;
    private string? _session;

    /// <summary>
    /// Says hello and opens a session in <paramref name="folder"/> with <paramref name="meta"/> (Grok's own options:
    /// the system prompt, the agent profile), then waits for the tools Grok lists for it.
    /// </summary>
    /// <exception cref="GrokToolsOfferedException">The session has tools of its own, or Grok did not say which.</exception>
    /// <exception cref="AssistantException">It answered with an error.</exception>
    /// <exception cref="IOException">It stopped.</exception>
    public async Task StartAsync(string folder, JsonObject meta, CancellationToken ct)
    {
        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false,
            },
        }, ct);
        JsonObject? listed = null;
        var opened = await RequestAsync("session/new", new JsonObject
        {
            ["cwd"] = folder,
            ["mcpServers"] = new JsonArray(),
            ["_meta"] = meta.DeepClone(),
        }, ct, message => listed ??= Listed(message));
        _session = Text((opened as JsonObject)?["sessionId"]) ?? throw new AssistantException("Grok did not open a session.");
        // Grok lists the session's tools right after it opened (available_commands_update, _meta.tools): only a session
        // with none is used. A Grok that ignored the profile, or no longer lists them, is not trusted with a turn.
        while (listed is null)
        {
            var message = await ReadAsync(ct);
            if (!await AnswerRequestAsync(message, ct)) listed = Listed(message);
        }
        Check(listed);
    }

    /// <summary>One turn: sends <paramref name="text"/>, returns Grok's answer and the tokens the turn took.</summary>
    /// <exception cref="AssistantException">The turn failed (its message says why: a plan's limit, a sign-in, for one).</exception>
    /// <exception cref="IOException">It stopped before the turn ended.</exception>
    public async Task<(string Answer, AssistantUsage Usage)> TurnAsync(string text, CancellationToken ct)
    {
        var session = _session ?? throw new InvalidOperationException("The session is not open.");
        var id = await SendAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = session,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        }, ct);
        var answer = new StringBuilder();
        while (true)
        {
            var message = await ReadAsync(ct);
            if (await AnswerRequestAsync(message, ct)) continue;
            if (Response(message, id) is { } response)
            {
                if (response.Error is { } error) throw new AssistantException($"Grok: {error}");
                var result = response.Result as JsonObject;
                var stop = Text(result?["stopReason"]);
                if (stop is "cancelled" or "refusal") throw new AssistantException($"Grok: the turn ended ({stop}).");
                return (answer.ToString(), Usage((result?["_meta"] as JsonObject)?["usage"] as JsonObject));
            }
            if (Update(message) is not { } update) continue;
            switch (Text(update["sessionUpdate"]))
            {
                case "agent_message_chunk":
                    answer.Append(Text((update["content"] as JsonObject)?["text"]));
                    break;
                case "available_commands_update":
                    Check(update);
                    break;
            }
        }
    }

    /// <summary>The update that lists the session's tools (and its slash commands), or null.</summary>
    private JsonObject? Listed(JsonObject message) =>
        Update(message) is { } update && Text(update["sessionUpdate"]) == "available_commands_update" ? update : null;

    /// <summary>A session that lists any tool, or none at all, is refused.</summary>
    private static void Check(JsonObject update)
    {
        if ((update["_meta"] as JsonObject)?["tools"] is not JsonArray tools) throw new GrokToolsOfferedException("Grok did not say which tools the session has.");
        if (tools.Count > 0) throw new GrokToolsOfferedException($"Grok kept tools of its own: {string.Join(", ", tools.Select(Text))}.");
    }

    /// <summary>
    /// A request of Grok's own, answered at once: a permission is cancelled (Helm's agent has no tool that would ask),
    /// anything else (a file to read, a terminal) is turned down. False for anything that is not a request.
    /// </summary>
    private async Task<bool> AnswerRequestAsync(JsonObject message, CancellationToken ct)
    {
        if (message["id"] is not JsonValue key || Text(message["method"]) is not { } method) return false;
        var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = key.DeepClone() };
        if (method == "session/request_permission")
            answer["result"] = new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };
        else
            answer["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Helm does not take this request." };
        await WriteAsync(answer, ct);
        return true;
    }

    /// <summary>Sends a request and waits for its answer; what comes before it goes to <paramref name="other"/>.</summary>
    private async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken ct, Action<JsonObject>? other = null)
    {
        var id = await SendAsync(method, parameters, ct);
        while (true)
        {
            var message = await ReadAsync(ct);
            if (await AnswerRequestAsync(message, ct)) continue;
            if (Response(message, id) is { } response)
                return response.Error is { } error ? throw new AssistantException($"Grok: {error}") : response.Result;
            other?.Invoke(message);
        }
    }

    private async Task<long> SendAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = ++_id;
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, ct);
        return id;
    }

    /// <summary>The answer to request <paramref name="id"/>, or null for anything else.</summary>
    private static (JsonNode? Result, string? Error)? Response(JsonObject message, long id)
    {
        if (message["method"] is not null || message["id"] is not JsonValue key || !key.TryGetValue<long>(out var answered) || answered != id) return null;
        if (message["error"] is not JsonObject error) return (message["result"], null);
        var why = Text(error["message"]) ?? "it answered with an error";
        // Grok may put the reason (a plan's limit, a sign-in) in the error's data.
        if ((Text(error["data"]) ?? Text((error["data"] as JsonObject)?["message"])) is { Length: > 0 } data && !why.Contains(data, StringComparison.Ordinal)) why += ": " + data;
        return (null, why);
    }

    /// <summary>The update of a <c>session/update</c> notification for this session, or null.</summary>
    private JsonObject? Update(JsonObject message) =>
        Text(message["method"]) == "session/update" && message["params"] is JsonObject parameters
        && (_session is null || Text(parameters["sessionId"]) is not { } session || session == _session)
            ? parameters["update"] as JsonObject
            : null;

    /// <summary>A turn's tokens (<c>_meta.usage</c>: its input counts the cached part too).</summary>
    private static AssistantUsage Usage(JsonObject? usage)
    {
        if (usage is null) return new AssistantUsage(0, 0, 0, 0);
        var cached = Count(usage, "cachedReadTokens");
        return new AssistantUsage(Math.Max(0, Count(usage, "inputTokens") - cached), Count(usage, "outputTokens"), cached, Count(usage, "cacheCreationTokens"));
    }

    private async Task<JsonObject> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await output.ReadLineAsync(ct) ?? throw new IOException("Grok stopped.");
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject message) return message;
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        await input.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
        await input.FlushAsync(ct);
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long Count(JsonObject usage, string name) => usage[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
}

/// <summary>A Grok session that has tools of its own (it ignored Helm's agent profile): Helm runs Grok headless instead.</summary>
public sealed class GrokToolsOfferedException(string message) : Exception(message);
