using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Core.Text;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary for AI agents over MCP. The user adds words in Helm; an agent reads the ones waiting
/// (<c>vocab_pending</c>), looks up parts Helm already has (<c>vocab_find_parts</c>) and writes each word's analysis
/// (<c>vocab_save_analysis</c>), which Helm checks before saving. Nothing is deleted through here.
/// </summary>
public sealed class VocabMcpTools(VocabStore store, VocabQuiz quiz) : IMcpToolProvider
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    /// <summary>How to break a word down; told to the agent with the tools that need it.</summary>
    internal const string Guide =
        "Write meanings, origins and explanations in Vietnamese, short but complete: the user learns a word by its pieces, so " +
        "every piece must say what it means and why it is there. Break by meaning, as many levels as carry meaning, and stop " +
        "where a piece has no smaller meaningful pieces; never strokes. " +
        "English: word → base word and affixes → Latin/Greek roots (relative → relate + -ive; relate → re- + lat; lat origin: " +
        "Latin latus, quá khứ phân từ của ferre \"mang\", nên fer = lat). Give a plain word with no meaningful pieces no parts. " +
        "Chinese: word → its characters in order → each character's components → their components while they mean something. " +
        "Every character needs pinyin, Hán Việt and an origin saying how its components make its meaning (补: 衤 áo + 卜 gợi âm bǔ " +
        "→ vá áo → vá, bù). Every component needs its Hán Việt name, its role (meaning or sound, 形声) and an origin saying which " +
        "character it is a form of and what it adds (衤 = 衣 áo, bộ Y; 攵 = 攴 đánh nhẹ, bộ Phộc; 氵 = 水 nước); a sound part says " +
        "how close its reading is (求 qiú ≈ 救 jiù). Give a traditional form only when it differs. Reuse a part Helm has (its id " +
        "from vocab_find_parts) when it is the same piece with the same meaning, so its family stays one. The word's " +
        "explanation says in one sentence how the parts' meanings make the word's.";

    public string? ModuleId => VocabIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("vocab_pending",
            "Words the user added to Vocabulary that wait for an analysis (oldest first), with the user's note and, for a word sent back, " +
            "what they found wrong and the analysis they rejected. Analyze each with vocab_find_parts and vocab_save_analysis. " + Guide,
            McpArgs.Schema(
                ("language", McpArgs.OneOf("Only this language (default both).", "english", "chinese"), false),
                ("limit", McpArgs.Number($"How many at most (default {DefaultLimit})."), false)),
            (args, _) => Task.FromResult<object?>(Pending(args))) { ReadOnly = true },
        new("vocab_find_parts",
            "Parts Helm already has, by how they are written (\"re\" also finds \"re-\"), with their meaning and smaller parts. Look up every " +
            "piece before saving an analysis and reuse the id of the same piece with the same meaning.",
            McpArgs.Schema(
                ("forms", McpArgs.List("Pieces as written: re, lat, ive, 救, 攵."), true),
                ("language", McpArgs.OneOf("The pieces' language.", "english", "chinese"), true)),
            (args, _) => Task.FromResult<object?>(FindParts(args))) { ReadOnly = true },
        new("vocab_save_analysis",
            "Save a word's analysis: its phonetic, Vietnamese meaning (Chinese: also Hán Việt, traditional when different), an explanation " +
            "and the tree of its parts. Helm checks it all first and says what to fix; a saved word leaves the pending list. " + Guide,
            SaveSchema(), (args, _) => Task.FromResult<object?>(Save(args))),
        new("vocab_save_analyses",
            "Save the analyses of many words in one call (each the same shape as vocab_save_analysis). Each word is checked and saved on " +
            "its own: the answer lists the saved ones and, for the others, what to fix. " + Guide,
            McpArgs.Schema(("analyses", new JsonObject
            {
                ["type"] = "array",
                ["items"] = SaveSchema(),
                ["description"] = "One analysis per word.",
            }, true)),
            (args, _) => Task.FromResult<object?>(SaveMany(args))),
        new("vocab_edit",
            "Reword words and parts the user already has, in one call: a meaning made easier to read, a corrected phonetic or origin. Only " +
            "the fields given change; the trees stay as they are (to rebuild a tree, use vocab_save_analysis).",
            EditSchema(), (args, _) => Task.FromResult<object?>(Edit(args))),
        new("vocab_words", "The user's words, newest first, with how well they remember each (quiz level 0-5).",
            McpArgs.Schema(
                ("query", McpArgs.Text("Words to look for in the word, its meaning or the user's note."), false),
                ("status", McpArgs.OneOf("Which words (default all).", "pending", "analyzed", "all"), false),
                ("limit", McpArgs.Number($"How many at most (default {DefaultLimit})."), false)),
            (args, _) => Task.FromResult<object?>(Words(args))) { ReadOnly = true },
        new("vocab_get", "One word with its whole tree of parts.",
            McpArgs.Schema(("word", McpArgs.Text("The word's id or the word itself."), true)),
            (args, _) => Task.FromResult<object?>(Get(McpArgs.RequiredString(args, "word")))) { ReadOnly = true },
        new("vocab_add_words", "Add words for the user to learn (they wait for an analysis). Words they already have are left as they are.",
            McpArgs.Schema(
                ("words", McpArgs.List("English or Chinese words or short phrases."), true),
                ("note", McpArgs.Text("Where they come from, for the user (optional)."), false)),
            (args, _) => Task.FromResult<object?>(Add(args))),
    ];

    private object Pending(JsonElement args)
    {
        var language = Language(McpArgs.String(args, "language"));
        var limit = Math.Clamp(McpArgs.Int(args, "limit") ?? DefaultLimit, 1, MaxLimit);
        var pending = store.Pending().Where(w => language is null || w.Value.Language == language).ToList();
        return new
        {
            total = pending.Count,
            words = pending.Take(limit).Select(w => new
            {
                id = w.Id,
                word = w.Value.Text,
                language = Name(w.Value.Language),
                note = NullIfEmpty(w.Value.Note),
                redo_note = NullIfEmpty(w.Value.RedoNote),
                rejected = w.Value.AnalyzedAt is null ? null : Tree(VocabTree.Build(store, w.Value)),
            }).ToList(),
        };
    }

    private object FindParts(JsonElement args)
    {
        var language = Language(McpArgs.RequiredString(args, "language")) ?? throw new McpToolException("language is english or chinese.");
        return McpArgs.Strings(args, "forms").Where(f => f.Trim().Length > 0).Distinct().Select(form => new
        {
            form,
            parts = store.PartsWritten(form, language).Select(p => new
            {
                id = p.Id,
                form = p.Value.Form,
                kind = VocabAnalyzer.Name(p.Value.Kind),
                meaning = p.Value.Meaning,
                phonetic = NullIfEmpty(p.Value.Phonetic),
                han_viet = NullIfEmpty(p.Value.HanViet),
                origin = NullIfEmpty(p.Value.Origin),
                parts = p.Value.Parts.Select(l => store.GetPart(l.PartId)?.Form).OfType<string>().ToList() is { Count: > 0 } forms ? forms : null,
                used_by = store.WordsUsing(p.Id).Take(5).Select(w => w.Value.Text).ToList(),
            }).ToList(),
        }).ToList();
    }

    private object SaveMany(JsonElement args)
    {
        if (!args.TryGetProperty("analyses", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new McpToolException("analyses must be a list of analyses.");
        var saved = new List<object>();
        var failed = new List<object>();
        foreach (var item in list.EnumerateArray())
        {
            try
            {
                Save(item);
                saved.Add(McpArgs.String(item, "id") ?? "");
            }
            catch (McpToolException ex)
            {
                failed.Add(new { id = McpArgs.String(item, "id"), fix = ex.Message });
            }
        }
        return new { saved, failed, pending_left = store.Pending().Count };
    }

    private object Edit(JsonElement args)
    {
        var edited = new List<string>();
        var failed = new List<object>();
        foreach (var item in Items(args, "words"))
        {
            var key = McpArgs.String(item, "id") ?? "";
            var id = store.GetWord(key) is not null ? key : store.FindWord(key)?.Id;
            if (id is null)
            {
                failed.Add(new { id = key, fix = "no such word" });
                continue;
            }
            store.UpdateWord(id, w => w with
            {
                Phonetic = Field(item, "phonetic") ?? w.Phonetic,
                Meaning = Field(item, "meaning") ?? w.Meaning,
                HanViet = Field(item, "han_viet") ?? w.HanViet,
                Traditional = Field(item, "traditional") ?? w.Traditional,
                Explanation = Field(item, "explanation") ?? w.Explanation,
                Note = Field(item, "note") ?? w.Note,
            });
            edited.Add(store.GetWord(id)!.Text);
        }
        foreach (var item in Items(args, "parts"))
        {
            var id = McpArgs.String(item, "id") ?? "";
            if (!store.UpdatePart(id, p => p with
                {
                    Phonetic = Field(item, "phonetic") ?? p.Phonetic,
                    Meaning = Field(item, "meaning") ?? p.Meaning,
                    HanViet = Field(item, "han_viet") ?? p.HanViet,
                    Traditional = Field(item, "traditional") ?? p.Traditional,
                    Origin = Field(item, "origin") ?? p.Origin,
                }))
                failed.Add(new { id, fix = "no such part" });
            else edited.Add(store.GetPart(id)!.Form);
        }
        return new { edited, failed };
    }

    private static IEnumerable<JsonElement> Items(JsonElement args, string name) =>
        args.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    /// <summary>A field to change, trimmed; null when not given.</summary>
    private static string? Field(JsonElement item, string name) => McpArgs.String(item, name)?.Trim();

    private object Save(JsonElement args)
    {
        var id = McpArgs.RequiredString(args, "id");
        var wordId = store.GetWord(id) is not null ? id : store.FindWord(id)?.Id ?? throw new McpToolException($"The user has no word {id}.");
        var analysis = new WordAnalysis
        {
            Phonetic = McpArgs.String(args, "phonetic") ?? "",
            Meaning = McpArgs.String(args, "meaning") ?? "",
            HanViet = McpArgs.String(args, "han_viet"),
            Traditional = McpArgs.String(args, "traditional"),
            Explanation = McpArgs.String(args, "explanation"),
            Parts = args.TryGetProperty("parts", out var parts) ? ReadParts(parts, 0) : [],
        };
        try
        {
            var saved = store.SaveAnalysis(wordId, analysis);
            return new { saved = true, id = wordId, word = saved.Text, tree = Tree(VocabTree.Build(store, saved)) };
        }
        catch (VocabAnalysisException ex)
        {
            throw new McpToolException(ex.Message);
        }
    }

    private object Words(JsonElement args)
    {
        var status = McpArgs.String(args, "status") ?? "all";
        if (status is not ("pending" or "analyzed" or "all")) throw new McpToolException("status is pending, analyzed or all.");
        var terms = TextSearch.Terms(McpArgs.String(args, "query"));
        var limit = Math.Clamp(McpArgs.Int(args, "limit") ?? DefaultLimit, 1, MaxLimit);
        var stats = quiz.Stats();
        return store.Words()
            .Where(w => status == "all" || (status == "analyzed") == w.Value.IsAnalyzed)
            .Where(w => terms.Count == 0 || TextSearch.Score(terms, w.Value.Text, $"{w.Value.Meaning} {w.Value.Note}") > 0)
            .Take(limit)
            .Select(w => new
            {
                id = w.Id,
                word = w.Value.Text,
                language = Name(w.Value.Language),
                status = w.Value.IsAnalyzed ? "analyzed" : "pending",
                phonetic = NullIfEmpty(w.Value.Phonetic),
                meaning = NullIfEmpty(w.Value.Meaning),
                level = stats.GetValueOrDefault(w.Id)?.Level ?? 0,
            }).ToList();
    }

    private object Get(string idOrText)
    {
        var (id, word) = store.GetWord(idOrText) is { } byId ? (idOrText, byId)
            : store.FindWord(idOrText) is { } byText ? (byText.Id, byText.Value)
            : throw new McpToolException($"The user has no word {idOrText}.");
        var stats = quiz.StatsOf(id);
        return new
        {
            id,
            language = Name(word.Language),
            status = word.IsAnalyzed ? "analyzed" : "pending",
            note = NullIfEmpty(word.Note),
            redo_note = NullIfEmpty(word.RedoNote),
            level = stats.Level,
            remembered = stats.Remembered,
            forgot = stats.Forgot,
            tree = word.IsAnalyzed || word.AnalyzedAt is not null ? Tree(VocabTree.Build(store, word)) : null,
        };
    }

    private object Add(JsonElement args)
    {
        var note = McpArgs.String(args, "note") ?? "";
        var added = new List<string>();
        var known = new List<string>();
        foreach (var text in McpArgs.Strings(args, "words").Where(w => w.Trim().Length > 0))
        {
            try
            {
                var (_, isNew) = store.AddWord(text, note);
                (isNew ? added : known).Add(VocabText.Tidy(text));
            }
            catch (ArgumentException ex)
            {
                throw new McpToolException($"{text}: {ex.Message}");
            }
        }
        return new { added, already_there = known };
    }

    // ---- shapes --------------------------------------------------------------------------------------------------

    /// <summary>A tree as the agent sees it: the same field names it writes.</summary>
    private static object Tree(VocabTreeNode node) => new
    {
        id = node.PartId,
        form = node.Form,
        kind = node.Kind is { } k ? VocabAnalyzer.Name(k) : null,
        role = node.Role == VocabPartRole.Plain ? null : node.Role.ToString().ToLowerInvariant(),
        phonetic = NullIfEmpty(node.Phonetic),
        meaning = NullIfEmpty(node.Meaning),
        han_viet = NullIfEmpty(node.HanViet),
        traditional = NullIfEmpty(node.Traditional),
        explanation = node.PartId is null ? NullIfEmpty(node.Origin) : null,
        origin = node.PartId is null ? null : NullIfEmpty(node.Origin),
        parts = node.Children.Count > 0 ? node.Children.Select(Tree).ToList() : null,
    };

    private static List<AnalysisPart> ReadParts(JsonElement list, int depth)
    {
        if (list.ValueKind != JsonValueKind.Array) throw new McpToolException("parts must be a list of parts.");
        if (depth > VocabAnalyzer.MaxDepth) throw new McpToolException($"At most {VocabAnalyzer.MaxDepth} levels below the word.");
        return list.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Object) throw new McpToolException("Each part is an object { form, kind, meaning, … }.");
            return new AnalysisPart
            {
                Id = McpArgs.String(item, "id"),
                Form = McpArgs.String(item, "form"),
                Kind = McpArgs.String(item, "kind") is { } kind ? ParseKind(kind) : null,
                Role = McpArgs.String(item, "role") is { } role ? ParseRole(role) : VocabPartRole.Plain,
                Phonetic = McpArgs.String(item, "phonetic"),
                Meaning = McpArgs.String(item, "meaning"),
                HanViet = McpArgs.String(item, "han_viet"),
                Traditional = McpArgs.String(item, "traditional"),
                Origin = McpArgs.String(item, "origin"),
                Parts = item.TryGetProperty("parts", out var parts) && parts.ValueKind != JsonValueKind.Null ? ReadParts(parts, depth + 1) : null,
            };
        }).ToList();
    }

    private static readonly string[] Kinds = Enum.GetValues<VocabPartKind>().Select(VocabAnalyzer.Name).ToArray();

    private static VocabPartKind ParseKind(string text) =>
        Enum.TryParse<VocabPartKind>(text.Trim(), ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind : throw new McpToolException($"kind is {string.Join(", ", Kinds)}.");

    private static VocabPartRole ParseRole(string text) => text.Trim().ToLowerInvariant() switch
    {
        "meaning" => VocabPartRole.Meaning,
        "sound" => VocabPartRole.Sound,
        "" or "plain" or "none" => VocabPartRole.Plain,
        _ => throw new McpToolException("role is meaning or sound (Chinese components only)."),
    };

    private static VocabLanguage? Language(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "english" or "en" => VocabLanguage.English,
        "chinese" or "zh" => VocabLanguage.Chinese,
        _ => throw new McpToolException("language is english or chinese."),
    };

    private static string Name(VocabLanguage language) => language == VocabLanguage.Chinese ? "chinese" : "english";

    private static string? NullIfEmpty(string text) => text.Length > 0 ? text : null;

    private static JsonObject EditSchema()
    {
        var word = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = McpArgs.Text("The word's id or the word itself."),
                ["phonetic"] = McpArgs.Text("New phonetic."),
                ["meaning"] = McpArgs.Text("New meaning, in Vietnamese."),
                ["han_viet"] = McpArgs.Text("New Hán Việt reading."),
                ["traditional"] = McpArgs.Text("New traditional form (empty: none)."),
                ["explanation"] = McpArgs.Text("New explanation, in Vietnamese."),
                ["note"] = McpArgs.Text("New note of the user's."),
            },
            ["required"] = new JsonArray("id"),
        };
        var part = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = McpArgs.Text("The part's id (vocab_get, vocab_find_parts)."),
                ["phonetic"] = McpArgs.Text("New phonetic."),
                ["meaning"] = McpArgs.Text("New meaning, in Vietnamese."),
                ["han_viet"] = McpArgs.Text("New Hán Việt reading or radical name."),
                ["traditional"] = McpArgs.Text("New traditional form (empty: none)."),
                ["origin"] = McpArgs.Text("New origin, in Vietnamese."),
            },
            ["required"] = new JsonArray("id"),
        };
        return McpArgs.Schema(
            ("words", new JsonObject { ["type"] = "array", ["items"] = word, ["description"] = "Words to reword." }, false),
            ("parts", new JsonObject { ["type"] = "array", ["items"] = part, ["description"] = "Parts to reword (every word that has the part shows the change)." }, false));
    }

    private static JsonObject SaveSchema()
    {
        var part = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = McpArgs.Text("A part Helm has (vocab_find_parts): reuse it. Fields given replace its own; parts left out keep its smaller parts."),
                ["form"] = McpArgs.Text("As written: re-, lat, -ive, 救, 攵."),
                ["kind"] = McpArgs.OneOf("English: prefix, root, suffix, word. Chinese: character, component.", Kinds),
                ["role"] = McpArgs.OneOf("Chinese components only: what it gives the character above.", "meaning", "sound"),
                ["phonetic"] = McpArgs.Text("IPA or pinyin with tone marks (characters need it)."),
                ["meaning"] = McpArgs.Text("In Vietnamese, short."),
                ["han_viet"] = McpArgs.Text("Chinese: the Hán Việt reading or the radical's name (characters need it)."),
                ["traditional"] = McpArgs.Text("Chinese: the traditional form, only when it differs."),
                ["origin"] = McpArgs.Text("In Vietnamese, optional: where it comes from, other forms of it (fer = lat)."),
                ["parts"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "object" },
                    ["description"] = "Its smaller parts, each the same shape as this one. Empty or left out at the bottom.",
                },
            },
        };
        return McpArgs.Schema(
            ("id", McpArgs.Text("The word's id (vocab_pending) or the word itself."), true),
            ("phonetic", McpArgs.Text("English: IPA, British (/ˈrel.ə.tɪv/). Chinese: pinyin with tone marks (bǔjiù)."), true),
            ("meaning", McpArgs.Text("In Vietnamese: the word's main meanings, short."), true),
            ("han_viet", McpArgs.Text("Chinese words: the Hán Việt reading (bổ cứu)."), false),
            ("traditional", McpArgs.Text("Chinese words: the traditional form, only when it differs (補救)."), false),
            ("explanation", McpArgs.Text("In Vietnamese, one sentence: how the parts' meanings make the word's."), false),
            ("parts", new JsonObject { ["type"] = "array", ["items"] = part, ["description"] = "The word's parts, top level first." }, false));
    }
}
