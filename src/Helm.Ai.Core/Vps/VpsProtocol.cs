using System.Text.Encodings.Web;
using System.Text.Json;

namespace Helm.Ai.Vps;

/// <summary>A job the VPS sent: one prompt, answered in text, as a conversation of its own.</summary>
/// <param name="System">The service's own system prompt; null for a short neutral one.</param>
public sealed record VpsJob(string Id, string Feature, string? System, string Prompt, string Model, string Effort);

/// <summary>
/// The frames between the worker and the VPS (JSON text): hello and status from the worker, job from the VPS, then
/// claim or decline, and result or error, by the job's id.
/// </summary>
public static class VpsProtocol
{
    // Vietnamese stays as it is (the frames are JSON, never HTML), not six bytes per letter.
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Hello(string device, IReadOnlyList<string> agents, string version) =>
        Write(new { type = "hello", device, agents, version });

    /// <summary>The agents that take jobs now, best first; none: online but taking no job.</summary>
    public static string Status(IReadOnlyList<string> agents) => Write(new { type = "status", agents });

    public static string Claim(string id) => Write(new { type = "claim", id });

    public static string Decline(string id, string reason) => Write(new { type = "decline", id, reason });

    public static string Result(string id, string text, string agent, decimal? savedUsd) => Write(new { type = "result", id, text, agent, savedUsd });

    public static string Error(string id, string message) => Write(new { type = "error", id, message });

    /// <summary>The job in a frame, or null for anything else (another type, no id, not JSON).</summary>
    public static VpsJob? ReadJob(string frame)
    {
        try
        {
            using var json = JsonDocument.Parse(frame);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") != "job" || Text(root, "id") is not { Length: > 0 } id) return null;
            return new VpsJob(id, Text(root, "feature") ?? "vps", Text(root, "system") is { Length: > 0 } system ? system : null,
                Text(root, "prompt") ?? "", Text(root, "model") ?? "", Text(root, "effort") is { Length: > 0 } effort ? effort : "low");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Write<T>(T frame) => JsonSerializer.Serialize(frame, Options);
}
