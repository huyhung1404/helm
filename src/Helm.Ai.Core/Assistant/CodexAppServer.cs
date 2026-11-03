using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Ai.Assistant;

/// <summary>
/// One conversation with a running <c>codex app-server</c> (Codex's own protocol for editors, codex 0.166: JSON-RPC
/// without the "jsonrpc" field, one JSON object a line on stdin/stdout). Its one thread stays loaded between turns, so a
/// turn costs no process start, no sign-in check and no session reload as a <c>codex exec</c> run does. The process and
/// its tools are set up by whoever started it; this only speaks the protocol.
/// </summary>
public sealed class CodexAppServer(TextReader output, TextWriter input)
{
    private long _id;
    private string? _thread;
    private (long Input, long Cached, long Output) _total;

    /// <summary>
    /// Says hello and opens the thread: in <paramref name="folder"/>, read-only, never asking for an approval, kept in
    /// memory only (Helm keeps the conversation; a new process starts over).
    /// </summary>
    /// <exception cref="AssistantException">It answered with an error.</exception>
    /// <exception cref="IOException">It stopped.</exception>
    public async Task StartAsync(string folder, string version, CancellationToken ct)
    {
        await RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "helm", ["title"] = "Helm", ["version"] = version },
        }, ct);
        await WriteAsync(new JsonObject { ["method"] = "initialized" }, ct);
        var started = await RequestAsync("thread/start", new JsonObject
        {
            ["cwd"] = folder,
            ["sandbox"] = "read-only",
            ["approvalPolicy"] = "never",
            ["ephemeral"] = true,
        }, ct);
        _thread = started?["thread"]?["id"]?.GetValue<string>() ?? throw new AssistantException("Codex did not open a conversation.");
    }

    /// <summary>One turn: sends <paramref name="text"/>, returns Codex's last message and the tokens the turn took.</summary>
    /// <exception cref="AssistantException">The turn failed (its message says why: a plan's limit, for one).</exception>
    /// <exception cref="IOException">It stopped before the turn ended.</exception>
    public async Task<(string Answer, AssistantUsage Usage)> TurnAsync(string text, CancellationToken ct)
    {
        var thread = _thread ?? throw new InvalidOperationException("The conversation is not open.");
        var id = await SendAsync("turn/start", new JsonObject
        {
            ["threadId"] = thread,
            ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        }, ct);
        string? answer = null, error = null;
        var before = _total;
        while (true)
        {
            var message = await ReadAsync(ct);
            if (await AnsweredAsync(message, id, ct) is { } response)
            {
                if (response.Error is { } refused) throw new AssistantException($"Codex: {refused}");
                continue;
            }
            var parameters = message["params"] as JsonObject ?? [];
            switch (message["method"]?.GetValue<string>())
            {
                case "item/completed":
                    if (parameters["item"] is { } item && Text(item["type"]) == "agentMessage") answer = Text(item["text"]) ?? answer;
                    break;
                case "thread/tokenUsage/updated":
                    if (parameters["tokenUsage"]?["total"] is JsonObject total)
                        _total = (Count(total, "inputTokens"), Count(total, "cachedInputTokens"),
                            Count(total, "outputTokens") + Count(total, "reasoningOutputTokens"));
                    break;
                case "error":
                    // One it retries by itself is only news.
                    if (!(parameters["willRetry"] is JsonValue retry && retry.TryGetValue<bool>(out var again) && again))
                        error = Text(parameters["error"]?["message"]) ?? "it stopped with an error";
                    break;
                case "turn/completed":
                    var turn = parameters["turn"];
                    var status = Text(turn?["status"]);
                    if (status is "failed" or "interrupted" || answer is null && error is not null)
                        throw new AssistantException($"Codex: {Text(turn?["error"]?["message"]) ?? error ?? $"the turn was {status}"}");
                    var input = _total.Input - before.Input;
                    var cached = _total.Cached - before.Cached;
                    return (answer ?? "", new AssistantUsage(Math.Max(0, input - cached), _total.Output - before.Output, cached, 0));
            }
        }
    }

    private async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = await SendAsync(method, parameters, ct);
        while (true)
            if (await AnsweredAsync(await ReadAsync(ct), id, ct) is { } response)
                return response.Error is { } error ? throw new AssistantException($"Codex: {error}") : response.Result;
    }

    private async Task<long> SendAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = ++_id;
        await WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, ct);
        return id;
    }

    /// <summary>
    /// The answer to request <paramref name="id"/>, or null for anything else. A request of Codex's own (an approval,
    /// a question for the user) is turned down at once: Helm's agent has none of the tools that would ask.
    /// </summary>
    private async Task<(JsonNode? Result, string? Error)?> AnsweredAsync(JsonObject message, long id, CancellationToken ct)
    {
        if (message["id"] is not JsonValue key) return null;
        if (message["method"] is not null)
        {
            await WriteAsync(new JsonObject
            {
                ["id"] = key.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Helm does not take this request." },
            }, ct);
            return null;
        }
        if (!key.TryGetValue<long>(out var answered) || answered != id) return null;
        return (message["result"], message["error"] is { } error ? Text(error["message"]) ?? "it answered with an error" : null);
    }

    private async Task<JsonObject> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await output.ReadLineAsync(ct) ?? throw new IOException("Codex stopped.");
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject message) return message;
            }
            catch (JsonException)
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
