using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary's follow-up work for AI agents, beside the analyses: words the user keeps forgetting need a new way to
/// remember them, and words analyzed before Helm asked for word forms, synonyms and antonyms need those filled in.
/// <c>vocab_follow_ups</c> lists both; <c>vocab_save_details</c> saves the details without touching the trees.
/// </summary>
public sealed class VocabFollowUpTools(VocabStore store, VocabQuiz quiz) : IMcpToolProvider
{
    private const int DefaultLimit = 12;
    private const int MaxLimit = 50;

    public string? ModuleId => VocabIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("vocab_follow_ups",
            "Words that need more than their analysis. often_forgotten: words the user forgot " + VocabQuiz.HardForgets + " times or more " +
            "since the last help; write a new mnemonic (a different, vivid story tying the parts to the meaning) and, when it " +
            "helps, a clearer explanation, better examples or the confusable word that trips the user up. missing_details: " +
            "analyzed words that still lack details Helm asks for now (word forms, synonyms, antonyms, and any of examples … " +
            "topic they lack). Save both with vocab_save_details. " + VocabMcpTools.Guide,
            McpArgs.Schema(("limit", McpArgs.Number($"How many of each at most (default {DefaultLimit})."), false)),
            (args, _) => Task.FromResult<object?>(FollowUps(args))) { ReadOnly = true },
        new("vocab_save_details",
            "Save details of analyzed words in one call: only the fields given change (a list given replaces the old one); the tree, " +
            "phonetic and meaning stay. A word from often_forgotten counts as helped; a word from missing_details leaves that list " +
            "even with empty forms, synonyms or antonyms (give [] when it has none). Each word is checked and saved on its own.",
            McpArgs.Schema(("words", new JsonObject
            {
                ["type"] = "array",
                ["items"] = McpArgs.Schema([("id", McpArgs.Text("The word's id or the word itself."), true), .. VocabMcpTools.DetailProperties()]),
                ["description"] = "One entry per word.",
            }, true)),
            (args, _) => Task.FromResult<object?>(SaveDetails(args))),
    ];

    private object FollowUps(JsonElement args)
    {
        var limit = Math.Clamp(McpArgs.Int(args, "limit") ?? DefaultLimit, 1, MaxLimit);
        var stats = quiz.Stats();
        var hard = quiz.HardWords();
        var lacking = store.Words().Where(w => w.Value.LacksDetails).ToList();
        return new
        {
            often_forgotten_total = hard.Count,
            often_forgotten = hard.Take(limit).Select(w => new
            {
                id = w.Id,
                word = w.Value.Text,
                language = w.Value.Language == VocabLanguage.Chinese ? "chinese" : "english",
                meaning = w.Value.Meaning,
                parts = VocabTree.Outline(store, w.Value),
                explanation = NullIfEmpty(w.Value.Explanation),
                mnemonic = NullIfEmpty(w.Value.Mnemonic),
                confusables = w.Value.Confusables.Count > 0 ? w.Value.Confusables.Select(c => c.Word).ToList() : null,
                forgot = stats[w.Id].ForgotSince(w.Value.HelpedAt),
                note = NullIfEmpty(w.Value.Note),
            }).ToList(),
            missing_details_total = lacking.Count,
            missing_details = lacking.Take(limit).Select(w => new
            {
                id = w.Id,
                word = w.Value.Text,
                language = w.Value.Language == VocabLanguage.Chinese ? "chinese" : "english",
                meaning = w.Value.Meaning,
                lacks = Lacks(w.Value),
            }).ToList(),
        };
    }

    /// <summary>The detail fields still empty on a word (word forms, synonyms and antonyms are always asked for once).</summary>
    private static List<string> Lacks(VocabWord w)
    {
        var lacks = new List<string> { "forms", "synonyms", "antonyms" };
        if (w.Examples.Count == 0) lacks.Add("examples");
        if (w.Collocations.Count == 0) lacks.Add("collocations");
        if (w.Mnemonic.Length == 0) lacks.Add("mnemonic");
        if (w.Confusables.Count == 0) lacks.Add("confusables");
        if (w.Level.Length == 0) lacks.Add("level");
        if (w.PartOfSpeech.Length == 0) lacks.Add("part_of_speech");
        if (w.Topic.Length == 0) lacks.Add("topic");
        return lacks;
    }

    private object SaveDetails(JsonElement args)
    {
        if (!args.TryGetProperty("words", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new McpToolException("words must be a list of { id, … }.");
        var stats = quiz.Stats();
        var saved = new List<string>();
        var failed = new List<object>();
        foreach (var item in list.EnumerateArray())
        {
            var key = McpArgs.String(item, "id") ?? "";
            var id = store.GetWord(key) is not null ? key : store.FindWord(key)?.Id;
            if (id is null)
            {
                failed.Add(new { id = key, fix = "no such word" });
                continue;
            }
            try
            {
                var word = store.GetWord(id)!;
                var hard = stats.GetValueOrDefault(id) is { } s && VocabQuiz.IsHard(word, s);
                saved.Add(store.SaveDetails(id, VocabMcpTools.ReadDetails(item), helped: hard).Text);
            }
            catch (VocabAnalysisException ex)
            {
                failed.Add(new { id = key, fix = ex.Message });
            }
        }
        return new
        {
            saved,
            failed,
            often_forgotten_left = quiz.HardWords().Count,
            missing_details_left = store.Words().Count(w => w.Value.LacksDetails),
        };
    }

    private static string? NullIfEmpty(string text) => text.Length > 0 ? text : null;
}
