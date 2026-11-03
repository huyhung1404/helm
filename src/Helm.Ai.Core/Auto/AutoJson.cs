using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Ai.Auto;

/// <summary>Small helpers the jobs use to cut the tools' answers down to what a prompt needs (every character is a token).</summary>
public static class AutoJson
{
    /// <summary>The items of an array, or of an object's array property.</summary>
    public static IReadOnlyList<JsonElement> Items(JsonElement? element, string? property = null)
    {
        if (element is not { } e) return [];
        if (property is not null)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(property, out e)) return [];
        }
        return e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray()] : [];
    }

    public static string? Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static decimal Number(JsonElement? item, string property) =>
        item is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0;

    public static int Int(JsonElement item, string property) => (int)Number(item, property);

    public static bool Flag(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The items with only <paramref name="fields"/>, as compact JSON.</summary>
    public static string Pick(IEnumerable<JsonElement> items, params string[] fields)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var picked = new JsonObject();
            foreach (var field in fields)
                if (item.TryGetProperty(field, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    picked[field] = JsonNode.Parse(value.GetRawText());
            array.Add(picked);
        }
        return array.ToJsonString(Compact);
    }

    public static string Raw(JsonElement? element) => element is { } e ? e.GetRawText() : "null";

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
