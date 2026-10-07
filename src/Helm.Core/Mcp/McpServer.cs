using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Core.Mcp;

/// <summary>
/// One tool Claude can call. <see cref="InputSchema"/> is a JSON Schema object; <see cref="Run"/> gets the arguments
/// and returns what goes back to Claude (serialized as JSON text). A <see cref="McpToolException"/> becomes an error
/// result Claude can read and correct.
/// </summary>
public sealed record McpTool(string Name, string Description, JsonObject InputSchema, Func<JsonElement, CancellationToken, Task<object?>> Run)
{
    private readonly McpRisk? _risk;

    /// <summary>Only reads (Claude may call it without asking); a tool that writes says so to the client.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>What a call can do; when not set, <see cref="McpRisk.Read"/> for a <see cref="ReadOnly"/> tool, else <see cref="McpRisk.Change"/>.</summary>
    public McpRisk Risk
    {
        get => _risk ?? (ReadOnly ? McpRisk.Read : McpRisk.Change);
        init => _risk = value;
    }

    /// <summary>
    /// Builds the question for this exact call (it may look at the server first). Null, or a null result: the consent
    /// still sees the call, through a question built from the tool.
    /// </summary>
    public Func<JsonElement, CancellationToken, Task<McpConsentRequest?>>? AskFirst { get; init; }

    /// <summary>A tool that takes no arguments.</summary>
    public static JsonObject NoArguments() => new() { ["type"] = "object", ["properties"] = new JsonObject() };
}

/// <summary>A tool call that cannot be done as asked (unknown id, invalid date…): Claude sees the message.</summary>
public sealed class McpToolException(string message) : Exception(message);

/// <summary>Tools a Helm module offers over MCP (registered as <c>IMcpToolProvider</c> singletons).</summary>
public interface IMcpToolProvider
{
    /// <summary>The module whose tools these are: they are offered only while it is turned on (null: always).</summary>
    string? ModuleId { get; }

    IEnumerable<McpTool> Tools { get; }
}

/// <summary>
/// A Model Context Protocol server over one connection: newline-delimited JSON-RPC 2.0 (the stdio transport), with
/// tools only. Requests are answered one at a time, in order. See https://modelcontextprotocol.io.
/// </summary>
public sealed class McpServer
{
    /// <summary>Newest first; a client asking for one of these gets it back, any other gets the newest.</summary>
    public static readonly IReadOnlyList<string> ProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private const int MaxLineBytes = 4 * 1024 * 1024;
    // Compact, with Vietnamese letters and quotes as they are (no escapes): fewer tokens for Claude to read.
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly IReadOnlyDictionary<string, McpTool> _tools;
    private readonly string _version;
    private readonly string _instructions;
    private const int MaxClientName = 100;
    private readonly ILogger _logger;
    private readonly IMcpConsent? _consent;
    private int _calls;

    /// <param name="consent">Asked before every call that is not <see cref="McpRisk.Read"/>. Without it, tools that change
    /// Helm's data run as before and <see cref="McpRisk.Remote"/> tools are refused.</param>
    public McpServer(IEnumerable<McpTool> tools, string version, string instructions, ILogger? logger = null, IMcpConsent? consent = null)
    {
        _tools = tools.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _version = version;
        _instructions = instructions;
        _logger = logger ?? NullLogger.Instance;
        _consent = consent;
        Client = new McpClientInfo(Guid.NewGuid(), "Unknown client", null);
    }

    public IReadOnlyCollection<string> ToolNames => _tools.Keys.ToList();

    /// <summary>This connection, with the name the client gave in <c>initialize</c>.</summary>
    public McpClientInfo Client { get; private set; }

    /// <summary>Tool calls so far on this connection.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary><see cref="Client"/> or <see cref="Calls"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Serves until the input ends (the client closed the connection) or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 16 * 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return;
            line = line.TrimStart('﻿'); // some clients start the stream with a byte order mark
            if (line.Length == 0) continue;
            var reply = line.Length > MaxLineBytes ? Error(null, -32600, "Message too large.") : await HandleAsync(line, ct).ConfigureAwait(false);
            if (reply is not null) await writer.WriteLineAsync(reply.ToJsonString(Output)).ConfigureAwait(false);
        }
    }

    /// <summary>One JSON-RPC message in, the reply out (null for a notification).</summary>
    public async Task<JsonObject?> HandleAsync(string line, CancellationToken ct)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error.");
        }
        if (message is JsonArray) return Error(null, -32600, "Batches are not supported.");
        if (message is not JsonObject request || request["method"]?.GetValue<string>() is not { } method) return Error(null, -32600, "Invalid request.");
        var id = request["id"]?.DeepClone();
        var isNotification = !request.ContainsKey("id");
        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(request["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => await CallToolAsync(request["params"] as JsonObject, ct).ConfigureAwait(false),
                _ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
                _ => throw new JsonRpcException(-32601, $"Method not found: {method}"),
            };
            if (method is "initialize" or "tools/call") Changed?.Invoke(this, EventArgs.Empty);
            if (isNotification) return null;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (JsonRpcException ex)
        {
            return isNotification ? null : Error(id, ex.Code, ex.Message);
        }
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        var asked = parameters?["protocolVersion"]?.GetValue<string>();
        if (parameters?["clientInfo"] is JsonObject info && ClientText(info["name"]) is { } name)
            Client = Client with { Name = name, Version = ClientText(info["version"]) };
        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "helm", ["title"] = "Helm", ["version"] = _version },
            ["instructions"] = _instructions,
        };
    }

    private JsonObject ListTools() => new()
    {
        ["tools"] = new JsonArray(_tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["inputSchema"] = t.InputSchema.DeepClone(),
            // A tool that reaches another machine says so: the client may then ask too.
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = t.Risk == McpRisk.Read,
                ["destructiveHint"] = t.Risk == McpRisk.Remote,
                ["openWorldHint"] = t.Risk == McpRisk.Remote,
            },
        }).ToArray()),
    };

    private async Task<JsonObject> CallToolAsync(JsonObject? parameters, CancellationToken ct)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? throw new JsonRpcException(-32602, "Missing tool name.");
        if (!_tools.TryGetValue(name, out var tool)) throw new JsonRpcException(-32602, $"Unknown tool: {name}");
        using var arguments = JsonDocument.Parse(parameters?["arguments"]?.ToJsonString() ?? "{}");
        Interlocked.Increment(ref _calls);
        try
        {
            if (await RefusalAsync(tool, arguments.RootElement, ct).ConfigureAwait(false) is { } refused)
            {
                _logger.LogInformation("MCP tool {Tool} not run: {Message}", name, refused);
                return ToolResult(refused, isError: true);
            }
            var result = await tool.Run(arguments.RootElement, ct).ConfigureAwait(false);
            return ToolResult(result as string ?? JsonSerializer.Serialize(result, Output), isError: false);
        }
        catch (Exception ex) when (ex is McpToolException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            // What was wrong with the call: Claude reads it and can try again.
            _logger.LogInformation("MCP tool {Tool} refused: {Message}", name, ex.Message);
            return ToolResult(ex.Message, isError: true);
        }
    }

    /// <summary>
    /// Asks the consent before a call that is not <see cref="McpRisk.Read"/>: why it may not run (Claude reads it), or
    /// null when it may.
    /// </summary>
    private async Task<string?> RefusalAsync(McpTool tool, JsonElement arguments, CancellationToken ct)
    {
        if (tool.Risk == McpRisk.Read) return null;
        if (_consent is null)
            return tool.Risk == McpRisk.Remote ? "Helm cannot ask the user here, so it does not run anything on another machine." : null;
        var request = (tool.AskFirst is { } ask ? await ask(arguments, ct).ConfigureAwait(false) : null) ?? DefaultRequest(tool, arguments);
        var answer = await _consent.AskAsync(request, Client, ct).ConfigureAwait(false);
        return answer == McpConsentAnswer.Deny ? $"The user declined: {request.Title}." : null;
    }

    private static McpConsentRequest DefaultRequest(McpTool tool, JsonElement arguments) => new(
        tool.Name,
        tool.Risk,
        $"Use “{tool.Name}”",
        tool.Description,
        tool.Risk == McpRisk.Remote
            ? "It runs something on another machine."
            : "It changes your Helm data, and the change syncs to your other devices.",
        arguments.ValueKind == JsonValueKind.Object && arguments.EnumerateObject().Any() ? arguments.GetRawText() : null,
        null,
        Environment.IsPrivilegedProcess,
        McpDanger.Normal);

    private static JsonObject ToolResult(string text, bool isError) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError };

    /// <summary>A name the client gave, as safe to show: a string, without control characters, not too long.</summary>
    private static string? ClientText(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
        text = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > MaxClientName) text = text[..MaxClientName];
        return text.Length > 0 ? text : null;
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private sealed class JsonRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}

/// <summary>Reading tool arguments: typed getters that explain what is wrong.</summary>
public static class McpArgs
{
    public static string? String(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new McpToolException($"{name} must be a string.")
            : null;

    public static string RequiredString(JsonElement args, string name) =>
        String(args, name) is { Length: > 0 } value ? value : throw new McpToolException($"{name} is required.");

    public static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.TryGetInt32(out var n) ? n : throw new McpToolException($"{name} must be a whole number.")
            : null;

    public static bool? Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public static IReadOnlyList<string> Strings(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new McpToolException($"{name} must be a list of strings.")).ToList()
            : [];

    /// <summary>A JSON Schema object with these properties (name → schema) and required names.</summary>
    public static JsonObject Schema(params (string Name, JsonObject Schema, bool Required)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema, _) in properties) props[name] = schema;
        var schemaObject = new JsonObject { ["type"] = "object", ["properties"] = props };
        var required = properties.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray();
        if (required.Length > 0) schemaObject["required"] = new JsonArray(required);
        return schemaObject;
    }

    public static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Number(string description) => new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject OneOf(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };

    public static JsonObject List(string description) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };
}
