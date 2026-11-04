using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Connector.Mcp;

/// <summary>
/// The user's yes before anything <see cref="McpTool.Destructive"/> runs through the connector (a note to the trash, a
/// mission replanned, any bulk deletion later). The tool call itself only describes what would happen and returns
/// <c>"status": "needs_confirmation"</c>; its <c>confirmationId</c> goes in the result's <c>_meta</c>, which the host
/// keeps from the model. The host (Fairy Core's code, on the user's yes) then sends <c>helm/confirm</c>, a JSON-RPC
/// method that is no tool, so a model, which can only call tools, cannot confirm for the user.
/// </summary>
/// <remarks>
/// An id is random, used once, valid for <see cref="Lifetime"/>, bound to the tool and a hash of its exact arguments,
/// and kept in this serve's memory only (a restart forgets every pending one).
/// </remarks>
internal sealed class ConfirmationGate(TimeProvider? time = null, ILogger? logger = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaxPending = 32;
    public const string MetaKey = "helm/confirmation";
    public const string Confirm = "helm/confirm", Cancel = "helm/cancel", List = "helm/pending";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>The JSON-RPC methods for <see cref="McpServer.Methods"/>.</summary>
    public IReadOnlyDictionary<string, Func<JsonObject?, CancellationToken, Task<JsonNode?>>> Methods => new Dictionary<string, Func<JsonObject?, CancellationToken, Task<JsonNode?>>>
    {
        [Confirm] = ConfirmAsync,
        [Cancel] = (p, _) => Task.FromResult<JsonNode?>(CancelOne(p)),
        [List] = (_, _) => Task.FromResult<JsonNode?>(PendingList()),
    };

    /// <summary><paramref name="tool"/> as the connector serves it: a call asks first and changes nothing.</summary>
    public McpTool Guard(McpTool tool) => tool with { Run = (args, ct) => AskAsync(tool, args, ct), Undo = null };

    private async Task<object?> AskAsync(McpTool tool, JsonElement args, CancellationToken ct)
    {
        // Resolving the ids also checks them: a call that could not run gets its error now, not after the user's yes.
        var summary = tool.Preview is { } preview ? await preview(args, ct).ConfigureAwait(false)
            : $"Run {tool.Name}" + (McpServer.ShownArguments(tool, args) is { } shown ? $" with {shown}" : "") + ".";
        var now = _time.GetUtcNow();
        var pending = new Pending(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), tool, args.GetRawText(),
            Hash(tool.Name, args), summary, now + Lifetime);
        lock (_pending)
        {
            foreach (var gone in _pending.Values.Where(p => p.ExpiresAt <= now).Select(p => p.Id).ToList()) _pending.Remove(gone);
            if (_pending.Count >= MaxPending) _pending.Remove(_pending.Values.MinBy(p => p.ExpiresAt)!.Id);
            _pending[pending.Id] = pending;
        }
        _logger.LogInformation("{Tool} waits for the user's confirmation: {Summary}", tool.Name, summary);
        return new McpToolResult(
            new
            {
                status = "needs_confirmation",
                tool = tool.Name,
                summary,
                message = "Nothing was changed. Helm asks the user to confirm this first: tell them what will happen and wait for their answer.",
            },
            new JsonObject
            {
                [MetaKey] = new JsonObject { ["confirmationId"] = pending.Id, ["expiresAt"] = pending.ExpiresAt.ToString("O") },
            });
    }

    /// <summary>
    /// <c>helm/confirm {confirmationId, name?, arguments?}</c>: runs the call the id stands for, once, and returns its
    /// <c>tools/call</c> result. Given a name and arguments, they must be the ones confirmed.
    /// </summary>
    private async Task<JsonNode?> ConfirmAsync(JsonObject? parameters, CancellationToken ct)
    {
        var pending = Take(parameters);
        using var arguments = JsonDocument.Parse(pending.Arguments);
        if (parameters?["name"] is { } name && (name is not JsonValue text || !text.TryGetValue<string>(out var tool) || tool != pending.Tool.Name))
            throw Mismatch();
        if (parameters?["arguments"] is { } given)
        {
            using var asked = JsonDocument.Parse(given.ToJsonString());
            if (Hash(pending.Tool.Name, asked.RootElement) != pending.Hash) throw Mismatch();
        }
        _logger.LogInformation("The user confirmed {Tool}: {Summary}", pending.Tool.Name, pending.Summary);
        var (result, _) = await McpServer.RunToolAsync(pending.Tool, arguments.RootElement, _logger, ct).ConfigureAwait(false);
        return result;
    }

    private static McpToolException Mismatch() =>
        new("The call does not match the one the user was asked about; nothing ran. Ask again.");

    private JsonNode CancelOne(JsonObject? parameters)
    {
        var pending = Take(parameters);
        _logger.LogInformation("The user declined {Tool}: {Summary}", pending.Tool.Name, pending.Summary);
        return new JsonObject { ["cancelled"] = true, ["tool"] = pending.Tool.Name };
    }

    private JsonNode PendingList()
    {
        var now = _time.GetUtcNow();
        lock (_pending)
            return new JsonObject
            {
                ["pending"] = new JsonArray([.. _pending.Values.Where(p => p.ExpiresAt > now).OrderBy(p => p.ExpiresAt).Select(p => (JsonNode)new JsonObject
                {
                    ["confirmationId"] = p.Id,
                    ["tool"] = p.Tool.Name,
                    ["summary"] = p.Summary,
                    ["expiresAt"] = p.ExpiresAt.ToString("O"),
                })]),
            };
    }

    /// <summary>The pending call of the params' id, no longer pending: an id is used (or cancelled) once, whatever happens next.</summary>
    private Pending Take(JsonObject? parameters)
    {
        var id = parameters?["confirmationId"] is JsonValue value && value.TryGetValue<string>(out var text) ? text
            : throw new McpToolException("confirmationId is missing.");
        Pending? pending;
        lock (_pending)
            if (_pending.Remove(id, out pending) && pending.ExpiresAt <= _time.GetUtcNow())
                throw new McpToolException("That confirmation has expired; nothing ran. Ask again.");
        return pending ?? throw new McpToolException("No call waits for that confirmation (unknown, already used or cancelled); nothing ran.");
    }

    /// <summary>The tool and its arguments, with object keys sorted, so the same call hashes the same however it is written.</summary>
    internal static string Hash(string tool, JsonElement arguments)
    {
        var text = new StringBuilder(tool).Append('\n');
        Canonical(arguments, text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Canonical(JsonElement element, StringBuilder text)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                text.Append('{');
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    text.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Canonical(property.Value, text);
                    text.Append(',');
                }
                text.Append('}');
                break;
            case JsonValueKind.Array:
                text.Append('[');
                foreach (var item in element.EnumerateArray())
                {
                    Canonical(item, text);
                    text.Append(',');
                }
                text.Append(']');
                break;
            case JsonValueKind.String:
                text.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            default:
                text.Append(element.GetRawText());
                break;
        }
    }

    private sealed record Pending(string Id, McpTool Tool, string Arguments, string Hash, string Summary, DateTimeOffset ExpiresAt);
}
