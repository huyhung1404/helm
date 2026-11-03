using System.Text.Json;

namespace Helm.Core.Ai;

/// <summary>
/// One question for an AI model that only needs text back (a translation, a word's analysis as JSON): no Helm tool
/// and no phone tool is offered, so the call is small and nothing is changed by it.
/// </summary>
/// <param name="Feature">The feature it is for (its choice of model, route and daily cap; AiFeatureIds).</param>
/// <param name="System">What the model is told it is and how it answers.</param>
/// <param name="Prompt">The request itself.</param>
public sealed record AiTextRequest(string Feature, string System, string Prompt);

/// <param name="Problem">Why there is no answer (no agent and no key, the day's cap, the provider failed); null when it worked.</param>
/// <param name="RanOn">The agent and Helm PC that answered (free, "Claude Code trên PC-HUNG"); null when the API did.</param>
public sealed record AiTextAnswer(string Text, string? Problem, string? RanOn = null)
{
    public bool Ok => Problem is null;

    public static AiTextAnswer Failed(string problem) => new("", problem);
}

/// <summary>
/// Asks a model for text, the way the user's features may: an agent on a Helm PC first (free on their plan), then the
/// API with the model picked, within the feature's daily cap (Helm.Ai's router). Tools reach it through this, so they
/// never reference the AI library. Only for calls the user started: work that runs by itself goes to a PC agent only.
/// </summary>
public interface IAiText
{
    /// <summary>Why <paramref name="feature"/> cannot ask now (turned off, no agent and no key), or null.</summary>
    string? Unavailable(string feature);

    /// <summary>Never throws for the model's or the network's problems: they come back in <see cref="AiTextAnswer.Problem"/>.</summary>
    Task<AiTextAnswer> AskAsync(AiTextRequest request, CancellationToken ct);
}

/// <summary>Reading the JSON object a model was asked to answer with, as models write it (in a code fence, with a word before).</summary>
public static class AiJson
{
    /// <summary>The first JSON object in <paramref name="text"/>, or null when there is none that parses.</summary>
    public static JsonElement? Object(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A string property, trimmed; null when missing or empty.</summary>
    public static string? String(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString()!.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>The strings of an array property (empty ones left out); empty when missing.</summary>
    public static IReadOnlyList<string> Strings(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0)]
            : [];

    /// <summary>The objects of an array property; empty when missing.</summary>
    public static IReadOnlyList<JsonElement> Objects(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)]
            : [];
}
