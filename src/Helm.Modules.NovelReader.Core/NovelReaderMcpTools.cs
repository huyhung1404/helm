using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Modules.NovelReader.Books;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Library;
using Helm.Modules.NovelReader.Names;
using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Novel Reader for AI agents over MCP, so an AI on the user's own PC (Claude Code, Claude Desktop…) finds a novel's
/// character names: list the novels, read the possible names (each with how often it appears, its Hán Việt reading,
/// what the logic scan thinks, and sentences where it is used), add the real ones to the novel's names (marked as found,
/// never over a name the user saved, never one the user deleted), and set aside words that are not names. Nothing is
/// deleted through here. The candidates of a novel are worked out once per scan (offset 0) and paged from there.
/// </summary>
public sealed class NovelReaderMcpTools(NovelStore store, DictionaryLibrary dictionaries) : IMcpToolProvider
{
    public const int DefaultPage = 80;
    public const int MaxPage = 150;

    private readonly object _gate = new();
    private (string BookId, IReadOnlyList<Candidate> Items)? _candidates;

    public string? ModuleId => NovelReaderIds.ModuleId;

    /// <summary>A possible name and what the logic scan made of it.</summary>
    private sealed record Candidate(NameFinding Finding, string Verdict);

    public IEnumerable<McpTool> Tools =>
    [
        new("novel_list", "The user's novels in Novel Reader (Chinese web novels read in Vietnamese): id, title, chapters, saved names, and whether names were looked for.",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(List())) { ReadOnly = true },
        new("novel_name_candidates",
            "Possible character names of a novel, most frequent first, each with how often it appears, its Hán Việt reading, what Helm's logic " +
            "scan thinks (sure, maybe, unlikely), whether it is saved already, and sentences where it is used. Start with offset 0 (that works " +
            "the list out again) and page on with the returned next_offset until it is null. Judge each word from its sentences.",
            McpArgs.Schema(
                ("novel", McpArgs.Text("The novel's id or title."), true),
                ("offset", McpArgs.Number("Where to start (0 for a new scan)."), false),
                ("limit", McpArgs.Number($"How many to return (default {DefaultPage}, at most {MaxPage})."), false)),
            CandidatesAsync) { ReadOnly = true },
        new("novel_names", "The names (and meanings) already saved for a novel: the Chinese, the Vietnamese, and whether a scan found it.",
            McpArgs.Schema(("novel", McpArgs.Text("The novel's id or title."), true)),
            (args, _) => Task.FromResult<object?>(Names(McpArgs.RequiredString(args, "novel")))) { ReadOnly = true },
        new("novel_add_names",
            "Add character names to a novel, as a Vietnamese reader writes them: the Hán Việt reading with every syllable capitalised for a person or " +
            "a place (林宛 → Lâm Uyển); a house or family word stays lowercase (林府 → Lâm phủ, 洛家 → Lạc gia). They are marked as found by a scan. " +
            "A name the user saved is never replaced, and one the user deleted is not added again; the reply says which were skipped.",
            NamesSchema(), (args, _) => Task.FromResult<object?>(AddNames(args))),
        new("novel_ignore_names",
            "Set aside words of a novel that are surely not names (pieces of idioms, a name with a word stuck to it, ordinary words): later scans leave them out for good, so never set aside a possible name (a given name used alone is a name).",
            McpArgs.Schema(
                ("novel", McpArgs.Text("The novel's id or title."), true),
                ("words", McpArgs.List("The Chinese words that are not names."), true)),
            (args, _) => Task.FromResult<object?>(IgnoreNames(args))),
    ];

    private object List() => store.Books().Select(b => new
    {
        id = b.Id,
        title = b.Value.Title,
        chapters = b.Value.ChapterCount,
        names = store.EntryCount(b.Id),
        found_names = store.FoundNameCount(b.Id),
        names_scanned = b.Value.NamesScannedAt is not null,
    }).ToList();

    private async Task<object?> CandidatesAsync(JsonElement args, CancellationToken ct)
    {
        var (id, book) = Find(McpArgs.RequiredString(args, "novel"));
        var offset = Math.Max(0, McpArgs.Int(args, "offset") ?? 0);
        var limit = Math.Clamp(McpArgs.Int(args, "limit") ?? DefaultPage, 1, MaxPage);
        IReadOnlyList<Candidate>? items;
        lock (_gate) items = offset > 0 && _candidates is { } kept && kept.BookId == id ? kept.Items : null;
        if (items is null)
        {
            items = await WorkOutAsync(id, ct).ConfigureAwait(false);
            lock (_gate) _candidates = (id, items);
        }
        var ignored = new HashSet<string>(store.GetBook(id)?.IgnoredNames ?? [], StringComparer.Ordinal);
        var page = items.Skip(offset).Take(limit).Select(c => new
        {
            word = c.Finding.Chinese,
            count = c.Finding.Count,
            reading = c.Finding.Vietnamese,
            logic = c.Verdict,
            saved = store.GetEntry(id, EntryKind.Name, c.Finding.Chinese) is not null || store.GetEntry(id, EntryKind.Phrase, c.Finding.Chinese) is not null
                || store.GetEntry(null, EntryKind.Name, c.Finding.Chinese) is not null,
            set_aside = ignored.Contains(c.Finding.Chinese),
            examples = c.Finding.Examples,
        }).ToList();
        var next = offset + page.Count;
        return new
        {
            novel = book.Title,
            total = items.Count,
            offset,
            next_offset = next < items.Count ? next : (int?)null,
            candidates = page,
        };
    }

    /// <summary>Every candidate of a novel with the logic scan's verdict, worked out off the caller's thread.</summary>
    private async Task<IReadOnlyList<Candidate>> WorkOutAsync(string id, CancellationToken ct)
    {
        var text = await store.ReadBookTextAsync(id, null, ct).ConfigureAwait(false);
        var set = await dictionaries.EnsureLoadedAsync().ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var layers = UserLayers.Build(store, id);
            var converter = new Converter(set, layers.BookNames, layers.UserNames, layers.Phrases);
            var paragraphs = ChapterSplitter.Split(text).SelectMany(c => c.Paragraphs.Prepend(c.Title)).ToList();
            bool Known(string word) => converter.EntriesFor(word).Count > 0;
            var scan = NameScanner.Scan(paragraphs, Known, converter.HanVietOf);
            var sure = new HashSet<string>(scan.Sure.Select(f => f.Chinese), StringComparer.Ordinal);
            var maybe = new HashSet<string>(scan.Maybe.Select(f => f.Chinese), StringComparer.Ordinal);
            var all = NameScanner.Candidates(paragraphs, Known, converter.HanVietOf, 300)
                .Where(c => c.Share >= 0.15 || sure.Contains(c.Finding.Chinese))
                .Select(c => new Candidate(c.Finding, sure.Contains(c.Finding.Chinese) ? "sure" : maybe.Contains(c.Finding.Chinese) ? "maybe" : "unlikely"))
                .ToList();
            // A given name the logic scan added on its own ("景桓" of "洛景桓") is not among the raw candidates.
            all.AddRange(scan.Sure.Where(f => all.All(c => c.Finding.Chinese != f.Chinese)).Select(f => new Candidate(f, "sure")));
            return (IReadOnlyList<Candidate>)all.OrderByDescending(c => c.Finding.Count).ToList();
        }, ct).ConfigureAwait(false);
    }

    private object Names(string novel)
    {
        var (id, book) = Find(novel);
        return new
        {
            novel = book.Title,
            names = store.Entries(id).Select(e => new { word = e.Chinese, vietnamese = e.Vietnamese, kind = e.Kind == EntryKind.Name ? "name" : "meaning", found = e.Auto }).ToList(),
            for_all_novels = store.Entries(null).Select(e => new { word = e.Chinese, vietnamese = e.Vietnamese, kind = e.Kind == EntryKind.Name ? "name" : "meaning" }).ToList(),
        };
    }

    private object AddNames(JsonElement args)
    {
        var (id, book) = Find(McpArgs.RequiredString(args, "novel"));
        if (!args.TryGetProperty("names", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new McpToolException("names must be a list of { word, vietnamese }.");
        var ignored = new HashSet<string>(book.IgnoredNames, StringComparer.Ordinal);
        var toAdd = new List<(string, string)>();
        var skipped = new List<object>();
        foreach (var item in list.EnumerateArray())
        {
            var word = ChineseText.NormalizeKey(McpArgs.String(item, "word") ?? "");
            var vietnamese = (McpArgs.String(item, "vietnamese") ?? "").Trim();
            string? reason = word.Length == 0 ? "no Chinese word"
                : vietnamese.Length == 0 ? "no Vietnamese"
                : ignored.Contains(word) ? "the user said it is not a name"
                : store.GetEntry(id, EntryKind.Name, word) is { Auto: false } || store.GetEntry(id, EntryKind.Phrase, word) is not null
                  || store.GetEntry(null, EntryKind.Name, word) is not null || store.GetEntry(null, EntryKind.Phrase, word) is not null ? "the user saved it already"
                : store.GetEntry(id, EntryKind.Name, word) is { Auto: true } ? "found already"
                : null;
            if (reason is null) toAdd.Add((word, vietnamese));
            else skipped.Add(new { word, reason });
        }
        var added = store.SaveFoundNames(id, toAdd);
        store.MarkNamesScanned(id);
        return new { novel = book.Title, added, skipped };
    }

    private object IgnoreNames(JsonElement args)
    {
        var (id, book) = Find(McpArgs.RequiredString(args, "novel"));
        var words = McpArgs.Strings(args, "words");
        foreach (var word in words) store.IgnoreName(id, word);
        return new { novel = book.Title, set_aside = words.Count };
    }

    /// <summary>A novel by id, exact title, or the one title that contains the text.</summary>
    private (string Id, NovelBook Book) Find(string novel)
    {
        var books = store.Books();
        var match = books.FirstOrDefault(b => b.Id == novel)
                    ?? books.FirstOrDefault(b => string.Equals(b.Value.Title, novel, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var containing = books.Where(b => b.Value.Title.Contains(novel, StringComparison.OrdinalIgnoreCase)).ToList();
            if (containing.Count > 1) throw new McpToolException($"\"{novel}\" matches several novels: {string.Join(", ", containing.Select(b => b.Value.Title))}. Use the id from novel_list.");
            match = containing.FirstOrDefault();
        }
        return match is null ? throw new McpToolException($"No novel \"{novel}\". novel_list shows them.") : (match.Id, match.Value);
    }

    private static JsonObject NamesSchema()
    {
        var item = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["word"] = McpArgs.Text("The name in Chinese, exactly as in the novel."),
                ["vietnamese"] = McpArgs.Text("How a Vietnamese reader writes it."),
            },
            ["required"] = new JsonArray("word", "vietnamese"),
        };
        return McpArgs.Schema(
            ("novel", McpArgs.Text("The novel's id or title."), true),
            ("names", new JsonObject { ["type"] = "array", ["items"] = item, ["description"] = "The names to add." }, true));
    }
}
