using System.Text.Json;
using Helm.Core.Mcp;

namespace Helm.Modules.Vocab;

/// <summary>
/// Claude Code's steps on the words, told for the Analysis dialog's log (<see cref="VocabAgentViewModel.Log"/>): which
/// words each call is about (by their text, not their ids), what it saved and what it has to fix, and what Claude says
/// between calls. A result starts with ✓ or ✗ and Claude's own words are quoted, so the log can tell them apart.
/// </summary>
internal sealed class VocabNarrator(VocabStore store) : IClaudeCodeNarrator
{
    private const int MaxNames = 4;
    private const int MaxText = 160;

    public string? Call(string tool, JsonElement input, int calls) => tool switch
    {
        "vocab_pending" => "Reading the words waiting to be broken down",
        "vocab_find_parts" => "Looking up parts: " + Names(Strings(input, "forms")),
        "vocab_save_analysis" => "Saving the analysis of " + Word(Text(input, "id")),
        "vocab_save_analyses" => Saving(Ids(input, "analyses"), "the analysis of", "analyses"),
        "vocab_save_details" => Saving(Ids(input, "words"), "new details of", "words' new details"),
        "vocab_get" => "Reading " + Word(Text(input, "id")),
        "vocab_words" => Text(input, "query") is { Length: > 0 } query ? $"Searching your words for “{query}”" : "Reading your words",
        "vocab_add_words" => "Adding words: " + Names(Strings(input, "words")),
        "vocab_follow_ups" => "Reading the words you keep forgetting, pinyin slips and missing details",
        "vocab_pending_texts" => "Reading your pasted texts",
        "vocab_propose_words" => "Picking words from a text: " + Names(Items(input, "words").Select(w => Text(w, "word"))),
        _ => $"Calling {tool}",
    };

    public string? Result(string tool, string result, bool isError)
    {
        if (isError) return "✗ " + Short(result);
        JsonElement root;
        try
        {
            using var json = JsonDocument.Parse(result);
            root = json.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object) return null;
        return tool switch
        {
            "vocab_pending" => Number(root, "total") is { } total ? $"✓ {total} waiting" : null,
            "vocab_find_parts" => null,
            "vocab_save_analysis" => root.TryGetProperty("word", out var word) ? $"✓ Saved {word.GetString()}" : null,
            "vocab_save_analyses" or "vocab_save_details" => Saved(root),
            "vocab_follow_ups" => $"✓ {Number(root, "often_forgotten_total") ?? 0} often forgotten · {Number(root, "pinyin_slips_total") ?? 0} pinyin slips · " +
                $"{Number(root, "missing_details_total") ?? 0} missing details",
            "vocab_add_words" => root.TryGetProperty("added", out var added) && added.ValueKind == JsonValueKind.Array ? $"✓ Added {added.GetArrayLength()}" : null,
            "vocab_propose_words" => Number(root, "proposed") is { } proposed ? $"✓ Proposed {proposed}" : null,
            _ => null,
        };
    }

    /// <summary>Claude's own words: the first line, quoted and cut short.</summary>
    public string? Text(string text) =>
        text.Trim().Split('\n', 2)[0].Trim() is { Length: > 0 } line ? $"“{Short(line)}”" : null;

    /// <summary>"✓ 2 saved · 1 to fix: reluctant (the parts must…)".</summary>
    private string Saved(JsonElement root)
    {
        var saved = root.TryGetProperty("saved", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0;
        var failed = root.TryGetProperty("failed", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().ToList() : [];
        if (failed.Count == 0) return $"✓ {saved} saved";
        var first = failed[0];
        return $"✓ {saved} saved · {failed.Count} to fix: {Word(Text(first, "id"))} ({Short(Text(first, "fix"), 90)})";
    }

    /// <summary>"Saving the analysis of relative", "Saving 3 analyses: relative, 补救, reluctant".</summary>
    private string Saving(IReadOnlyList<string> ids, string one, string many) =>
        ids.Count == 1 ? $"Saving {one} {Word(ids[0])}" : $"Saving {ids.Count} {many}: {Names(ids.Select(Word))}";

    /// <summary>A word by its id (what the tools pass) or by itself, as the user wrote it.</summary>
    private string Word(string key) => store.GetWord(key)?.Text ?? store.FindWord(key)?.Value.Text ?? key;

    private static string Names(IEnumerable<string> names)
    {
        var list = names.Where(n => n.Length > 0).ToList();
        return list.Count <= MaxNames ? string.Join(", ", list) : $"{string.Join(", ", list.Take(MaxNames))} and {list.Count - MaxNames} more";
    }

    private static IReadOnlyList<string> Ids(JsonElement input, string list) => [.. Items(input, list).Select(i => Text(i, "id"))];

    private static IEnumerable<JsonElement> Items(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    private static IEnumerable<string> Strings(JsonElement input, string name) =>
        Items(input, name).Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString() ?? "");

    private static string Text(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int? Number(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static string Short(string text, int max = MaxText)
    {
        var line = text.Trim().ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}
