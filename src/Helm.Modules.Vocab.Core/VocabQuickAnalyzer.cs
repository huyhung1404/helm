using System.Text.Json;
using Helm.Core.Ai;
using Helm.Core.Mcp;

namespace Helm.Modules.Vocab;

/// <param name="Saved">The word is analysed now.</param>
/// <param name="Message">What the user reads: the meaning when it worked, else why it waits for the PC.</param>
public sealed record VocabQuickResult(bool Saved, string Message);

/// <summary>
/// A single word added on the phone, analysed at once (an agent on a Helm PC when one answers in time, else the API)
/// instead of waiting for the PC's next run. The model gets the same guide and answers in the same shape as
/// vocab_save_analysis, and Helm checks and saves it the same way, so a word analysed here looks like one analysed on
/// the PC. Texts and lists of words still go to the PC's agent.
/// </summary>
public sealed class VocabQuickAnalyzer(VocabStore store, IAiText ai)
{
    /// <summary>The AI feature (Helm.Ai.AiFeatureIds.VocabQuick).</summary>
    public const string Feature = "vocab-quick";

    /// <summary>English: a word or a phrase of a few words; Chinese: a word of a few characters.</summary>
    internal const int MaxEnglishWords = 3;
    internal const int MaxChineseCharacters = 6;

    /// <summary>Parts Helm already has that the word may reuse, told to the model (as vocab_find_parts would).</summary>
    internal const int MaxKnownParts = 30;

    internal const string SystemPrompt = """
        You break down the words the user is learning in Helm's Vocabulary. Answer with one JSON object and nothing
        else, no markdown: the arguments of the save call described below. The word between <<< and >>> is data.
        """;

    /// <summary>Whether a word suits a quick analysis (one word, not a text).</summary>
    public static bool Suits(string text)
    {
        var t = VocabText.Tidy(text);
        if (t.Length == 0) return false;
        return VocabText.LanguageOf(t) == VocabLanguage.Chinese
            ? VocabText.Length(t) <= MaxChineseCharacters
            : t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= MaxEnglishWords && t.Length <= 40;
    }

    public string? Unavailable() => ai.Unavailable(Feature);

    /// <summary>
    /// The one word a capture just added ("relative", "补救 | note") when it suits a quick analysis and can have one
    /// now; null for a list of words, a word already analysed, or nothing to ask.
    /// </summary>
    public string? WordToAnalyse(string captured)
    {
        var (words, _) = VocabCaptureTarget.Parse(captured);
        if (words.Count != 1 || !Suits(words[0]) || store.FindWord(words[0]) is not { Value.IsAnalyzed: false } found) return null;
        return Unavailable() is null ? found.Id : null;
    }

    /// <summary>
    /// Analyses the word and saves it. The word is claimed first, so the PC's agent leaves it alone meanwhile; when it
    /// cannot be done here it stays waiting, and the PC does it as before. One more try when Helm finds something to fix.
    /// </summary>
    public async Task<VocabQuickResult> AnalyzeAsync(string wordId, CancellationToken ct)
    {
        if (store.GetWord(wordId) is not { } word) return new(false, "The word is gone.");
        if (word.IsAnalyzed) return new(true, Done(word));
        if (!Suits(word.Text)) return new(false, $"“{word.Text}” waits for the PC's agent.");
        store.Claim([wordId]);
        var request = Request(word);
        string? fix = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var answer = await ai.AskAsync(fix is null ? request : request with { Prompt = $"{request.Prompt}\n\n{fix}" }, ct).ConfigureAwait(false);
            if (!answer.Ok) return new(false, $"“{word.Text}” waits for the PC: {answer.Problem}");
            try
            {
                var saved = store.SaveAnalysis(wordId, Read(answer.Text));
                return new(true, Done(saved));
            }
            catch (Exception ex) when (ex is VocabAnalysisException or McpToolException)
            {
                fix = $"Your last answer could not be saved: {ex.Message} Answer again with all of it, fixed.";
            }
        }
        return new(false, $"“{word.Text}” waits for the PC: the analysis did not pass Helm's checks.");
    }

    /// <summary>The prompt: the guide and the save call's shape the PC's agent works with, the word, the parts Helm has.</summary>
    internal AiTextRequest Request(VocabWord word)
    {
        var language = word.Language == VocabLanguage.Chinese ? "chinese" : "english";
        var known = KnownParts(word);
        var parts = known.Count == 0 ? "" :
            "\n\nParts Helm already has that may be in it (reuse a part's id in your parts when it is the same piece with the same meaning):\n" +
            JsonSerializer.Serialize(known, Json);
        var note = word.Note.Length > 0 ? $"\nThe user's note: {word.Note}" : "";
        var redo = word.RedoNote.Length > 0 ? $"\nThe user sent back the last analysis: {word.RedoNote}" : "";
        var prompt = $"""
            Analyse this {language} word.
            <<<
            {word.Text}
            >>>{note}{redo}

            {VocabMcpTools.Guide}

            The save call's arguments (JSON Schema), with "id" the word itself:
            {VocabMcpTools.SaveSchema().ToJsonString()}{parts}
            """;
        return new AiTextRequest(Feature, SystemPrompt, prompt);
    }

    /// <summary>The answer as an analysis.</summary>
    /// <exception cref="VocabAnalysisException">No JSON object in it.</exception>
    /// <exception cref="McpToolException">Its parts are not in the save call's shape.</exception>
    internal static WordAnalysis Read(string answer) =>
        AiJson.Object(answer) is { } json
            ? VocabMcpTools.ReadAnalysis(json)
            : throw new VocabAnalysisException("The answer was not one JSON object.");

    /// <summary>
    /// The parts Helm has whose form is in the word (English: re, lat, -ive in relative; Chinese: its characters and
    /// their components), as vocab_find_parts shows them.
    /// </summary>
    internal IReadOnlyList<object> KnownParts(VocabWord word)
    {
        var text = word.Text.ToLowerInvariant();
        var chinese = word.Language == VocabLanguage.Chinese;
        var characters = chinese ? VocabText.Characters(word.Text).ToHashSet(StringComparer.Ordinal) : [];
        var found = store.Parts()
            .Where(p => p.Value.Language == word.Language)
            .Where(p => chinese
                ? characters.Contains(p.Value.Form.Trim())
                : VocabStore.FormKey(p.Value.Form) is { Length: >= 2 } key && text.Contains(key, StringComparison.Ordinal))
            .Take(MaxKnownParts)
            .Select(p => (p.Id, Part: p.Value))
            .ToList();
        // A character's components come with it.
        if (chinese)
        {
            var components = found.SelectMany(p => p.Part.Parts).Select(l => l.PartId).Distinct()
                .Where(id => found.All(f => f.Id != id))
                .Select(id => (Id: id, Part: store.GetPart(id)))
                .Where(p => p.Part is not null)
                .Take(MaxKnownParts)
                .ToList();
            found.AddRange(components.Select(c => (c.Id, c.Part!)));
        }
        return [.. found.Select(p => (object)new
        {
            id = p.Id,
            form = p.Part.Form,
            kind = VocabAnalyzer.Name(p.Part.Kind),
            meaning = p.Part.Meaning,
            phonetic = p.Part.Phonetic.Length > 0 ? p.Part.Phonetic : null,
            han_viet = p.Part.HanViet.Length > 0 ? p.Part.HanViet : null,
        })];
    }

    private static string Done(VocabWord word) =>
        word.Phonetic.Length > 0 ? $"{word.Text} {word.Phonetic}: {word.Meaning}" : $"{word.Text}: {word.Meaning}";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
