using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary's pasted texts for AI agents: the user pastes an article or song lyrics, an agent reads it
/// (<c>vocab_pending_texts</c>) and proposes the words worth learning (<c>vocab_propose_words</c>); the user keeps or drops
/// each in Helm, and only the kept ones become words to break down. Nothing is added to the user's words from here.
/// </summary>
public sealed class VocabTextTools(VocabStore store) : IMcpToolProvider
{
    private const int DefaultLimit = 3;
    private const int MaxLimit = 10;

    public string? ModuleId => VocabIds.ModuleId;

    /// <summary>How to pick words from a text; told to the agent with the tools that need it.</summary>
    internal static readonly string PickGuide =
        "Pick the words worth learning, not every word: skip very basic words (是, 的, I, the) and names. For Chinese, write each " +
        "word in simplified characters (convert a traditional text first); a phrase or idiom counts as one word (梨花带雨). Look up " +
        "what the user already has with vocab_words before proposing: do not propose it again. Give each word its pinyin or IPA, " +
        "a short Vietnamese meaning, the line of the text it comes from, exactly as written there, and in Vietnamese why it is " +
        "worth learning. At most " + VocabSource.MaxProposals + " words per text.";

    public IEnumerable<McpTool> Tools =>
    [
        new("vocab_pending_texts",
            "Texts the user pasted into Vocabulary (articles, song lyrics) that wait for an agent to pick their words (oldest first), " +
            "with the user's title. Read each and propose its words with vocab_propose_words. " + PickGuide,
            McpArgs.Schema(("limit", McpArgs.Number($"How many at most (default {DefaultLimit})."), false)),
            (args, _) => Task.FromResult<object?>(Pending(args))) { ReadOnly = true },
        new("vocab_propose_words",
            "Propose the words to learn from one pasted text. They are shown to the user to keep or drop; nothing is added until " +
            "the user adds them. A word the user already has is kept in the list but not ticked. " + PickGuide,
            McpArgs.Schema(
                ("text_id", McpArgs.Text("The text's id (vocab_pending_texts)."), true),
                ("words", new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = McpArgs.Schema(
                        ("word", McpArgs.Text("The word as it should be added (Chinese: simplified)."), true),
                        ("phonetic", McpArgs.Text("Pinyin with tone marks, or IPA."), false),
                        ("meaning", McpArgs.Text("In Vietnamese, short."), true),
                        ("sentence", McpArgs.Text("The line of the text the word comes from, as written there."), true),
                        ("reason", McpArgs.Text("In Vietnamese: why it is worth learning."), false)),
                    ["description"] = "The words picked from the text.",
                }, true)),
            (args, _) => Task.FromResult<object?>(Propose(args))),
    ];

    private object Pending(JsonElement args)
    {
        var limit = Math.Clamp(McpArgs.Int(args, "limit") ?? DefaultLimit, 1, MaxLimit);
        var waiting = store.WaitingSources();
        var shown = waiting.Take(limit).ToList();
        // The agent that reads a text picks its words: Helm's own agent leaves it alone for a while.
        store.ClaimSources(shown.Select(s => s.Id));
        return new
        {
            total = waiting.Count,
            texts = shown.Select(s => new { id = s.Id, title = s.Value.Title.Length > 0 ? s.Value.Title : null, text = s.Value.Body }).ToList(),
        };
    }

    private object Propose(JsonElement args)
    {
        var id = McpArgs.RequiredString(args, "text_id");
        if (store.GetSource(id) is null) throw new McpToolException($"The user has no text {id}.");
        var picked = new List<VocabProposal>();
        if (args.TryGetProperty("words", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                picked.Add(new VocabProposal
                {
                    Word = McpArgs.String(item, "word") ?? "",
                    Phonetic = McpArgs.String(item, "phonetic")?.Trim() ?? "",
                    Meaning = McpArgs.String(item, "meaning")?.Trim() ?? "",
                    Sentence = McpArgs.String(item, "sentence")?.Trim() ?? "",
                    Reason = McpArgs.String(item, "reason")?.Trim() ?? "",
                });
            }
        }
        if (picked.Count == 0) throw new McpToolException("words is empty: propose at least one word.");
        var saved = store.Propose(id, picked);
        return new
        {
            proposed = saved.Count(p => p.ExistingWordId is null),
            already_there = saved.Where(p => p.ExistingWordId is not null).Select(p => p.Word).ToList(),
            left_out = picked.Count - saved.Count,
        };
    }
}
