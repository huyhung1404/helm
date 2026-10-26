using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Library;

namespace Helm.Modules.NovelReader.Reports;

/// <summary>
/// The reports of wrong translations for an AI on the user's PC: read each sentence with what Helm showed, the right
/// translation, the reader's note, the pieces Helm cut it into, and what Helm shows for it now with today's
/// dictionaries and the novel's names; and write back suggested translations, which never replace the reader's own.
/// </summary>
public sealed class TranslationReportMcpTools(TranslationReportStore reports, NovelStore store, DictionaryLibrary dictionaries) : IMcpToolProvider
{
    public const int DefaultPage = 50;
    public const int MaxPage = 200;

    public string? ModuleId => NovelReaderIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("novel_translation_reports",
            "Sentences the user marked as translated wrong in Novel Reader (Chinese → Vietnamese by VietPhrase dictionaries), newest first: " +
            "the Chinese, what Helm showed, the right translation and the user's note, the word clicked, the pieces Helm cut the sentence " +
            "into (chinese=vietnamese (kind: phrase, name, han-viet, rule, unknown)), the paragraph around it, an AI's suggestion if any, " +
            "and helm_now / right_now: Helm's translation today and whether it is the right one. Page with offset until next_offset is null.",
            McpArgs.Schema(
                ("novel", McpArgs.Text("Only this novel's reports: its id or title (novel_list). Leave out for all."), false),
                ("ids", McpArgs.List("Only these reports (their ids)."), false),
                ("offset", McpArgs.Number("Where to start (0 first)."), false),
                ("limit", McpArgs.Number($"How many to return (default {DefaultPage}, at most {MaxPage})."), false)),
            ListAsync) { ReadOnly = true },
        new("novel_suggest_translations",
            "Suggest the right Vietnamese translation of reported sentences, with a short note in Vietnamese on what Helm got wrong and the " +
            "kind of error. A suggestion is shown to the user next to the report; it never replaces the user's own translation. " +
            "A later suggestion for the same report replaces the earlier one.",
            SuggestSchema(), (args, _) => Task.FromResult<object?>(Suggest(args))),
    ];

    private async Task<object?> ListAsync(JsonElement args, CancellationToken ct)
    {
        var novel = McpArgs.String(args, "novel");
        var bookId = string.IsNullOrWhiteSpace(novel) ? null : NovelReaderMcpTools.FindBook(store, novel).Id;
        var ids = McpArgs.Strings(args, "ids").ToHashSet(StringComparer.Ordinal);
        var offset = Math.Max(0, McpArgs.Int(args, "offset") ?? 0);
        var limit = Math.Clamp(McpArgs.Int(args, "limit") ?? DefaultPage, 1, MaxPage);
        var all = reports.Reports(bookId).Where(r => ids.Count == 0 || ids.Contains(r.Id)).ToList();
        var page = all.Skip(offset).Take(limit).ToList();
        var set = dictionaries.HasFiles ? await dictionaries.EnsureLoadedAsync().ConfigureAwait(false) : null;
        ct.ThrowIfCancellationRequested();
        // One converter per novel: its names decide how its sentences come out.
        var converters = new Dictionary<string, Converter>(StringComparer.Ordinal);
        Converter? ConverterFor(string? id)
        {
            if (set is null) return null;
            var key = id ?? "";
            if (converters.TryGetValue(key, out var made)) return made;
            var layers = UserLayers.Build(store, id is not null && store.GetBook(id) is not null ? id : null);
            return converters[key] = new Converter(set, layers.BookNames, layers.UserNames, layers.Phrases);
        }
        var items = page.Select(r =>
        {
            var report = r.Value;
            var now = ConverterFor(report.BookId)?.ConvertLine(report.Chinese).Text;
            return new
            {
                id = r.Id,
                novel = report.BookTitle,
                chapter = report.ChapterTitle,
                chinese = report.Chinese,
                helm = report.Helm,
                correct = report.Correct,
                note = report.Note,
                word = report.Word,
                pieces = string.Join(" · ", report.Pieces.Select(p => $"{p.Chinese}={p.Text} ({p.Kind})")),
                paragraph = report.Paragraph != report.Chinese ? report.Paragraph : "",
                suggestion = report.Suggestion,
                suggestion_note = report.SuggestionNote,
                error_kind = report.ErrorKind,
                helm_now = now,
                right_now = now is not null && TranslationReportDocument.IsFixed(report, now),
                reported = report.CreatedAt,
            };
        }).ToList();
        var next = offset + items.Count;
        return new
        {
            total = all.Count,
            offset,
            next_offset = next < all.Count ? next : (int?)null,
            dictionaries_loaded = set is not null,
            reports = items,
        };
    }

    private object Suggest(JsonElement args)
    {
        if (!args.TryGetProperty("suggestions", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new McpToolException("suggestions must be a list of { id, translation, note, kind }.");
        var saved = 0;
        var skipped = new List<object>();
        foreach (var item in list.EnumerateArray())
        {
            var id = McpArgs.String(item, "id") ?? "";
            var translation = (McpArgs.String(item, "translation") ?? "").Trim();
            string? reason = id.Length == 0 ? "no id"
                : translation.Length == 0 ? "no translation"
                : !reports.SaveSuggestion(id, translation, McpArgs.String(item, "note") ?? "", McpArgs.String(item, "kind") ?? "") ? "no such report"
                : null;
            if (reason is null) saved++;
            else skipped.Add(new { id, reason });
        }
        return new { saved, skipped };
    }

    private static JsonObject SuggestSchema()
    {
        var item = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = McpArgs.Text("The report's id (novel_translation_reports)."),
                ["translation"] = McpArgs.Text("The right Vietnamese translation of the report's Chinese, with its punctuation and quotes."),
                ["note"] = McpArgs.Text("In Vietnamese, one short sentence: what Helm got wrong."),
                ["kind"] = McpArgs.OneOf(
                    "word-order: 的 phrases or modifiers left in Chinese order; location: 在…上/里/中 frames; meaning: a word took the wrong one " +
                    "of its meanings; segmentation: the sentence was cut into the wrong words; pronoun: the wrong form of address; name: a name " +
                    "not known or cut; other.", [.. ErrorKinds.All]),
            },
            ["required"] = new JsonArray("id", "translation", "note", "kind"),
        };
        return McpArgs.Schema(("suggestions", new JsonObject { ["type"] = "array", ["items"] = item, ["description"] = "One per report." }, true));
    }
}
