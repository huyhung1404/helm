using Helm.Core.Mcp;
using Helm.Modules.NovelReader.Reports;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Suggested translations on Windows: Claude Code (<see cref="ClaudeCodeRunner"/>), allowed only to read the reports and
/// the novel's names and to write suggestions with <c>novel_suggest_translations</c>. A suggestion never replaces the
/// reader's own translation; it arrives in the report while Claude Code works.
/// </summary>
public sealed class ClaudeCodeTranslationAgent(ClaudeCodeRunner claude) : ITranslationAgent
{
    /// <summary>A report or two take a minute; a long list a few.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>The only tools it may call.</summary>
    public static IReadOnlyList<string> AllowedTools { get; } =
        ClaudeCodeRunner.ToolNames("novel_translation_reports", "novel_names", "novel_suggest_translations");

    public string Name => "Claude Code";

    public string? Unavailable() => claude.Unavailable();

    /// <summary>What Claude Code is asked to do (in English, the language it works best in; the notes are for a Vietnamese reader).</summary>
    public static string Instructions(string bookId, string title, IReadOnlyList<string> reportIds) =>
        $"""
        Help the reader of the novel "{title}" (id {bookId}) in Helm's Novel Reader, using only Helm's novel tools. Novel
        Reader turns Chinese web novels into Vietnamese with QuickTranslator dictionaries (VietPhrase), word by word, and
        the reader marked sentences it gets wrong. Suggest the right translation of each.

        1. Call novel_translation_reports with novel "{bookId}" and ids {string.Join(", ", reportIds.Select(id => $"\"{id}\""))}.
           Each report has the Chinese sentence, what Helm showed (helm), the pieces Helm cut it into
           (chinese=vietnamese (kind)), the paragraph around it, and the reader's own translation and note if any.
        2. Call novel_names with novel "{bookId}" and write every name and term exactly as saved there.
        3. For each report, translate the Chinese sentence into natural Vietnamese the way Vietnamese readers of web novels
           expect: faithful to the meaning, Hán Việt for names, places, sects, titles and cultivation terms, and the forms
           of address the scene calls for (ta/ngươi, hắn/nàng, chàng/thiếp…), judged from the paragraph. Keep the
           sentence's punctuation and quotes. When the reader wrote a translation, still give yours.
        4. Say in one short Vietnamese sentence what Helm got wrong, and pick the kind of error: word-order (的 phrases or
           modifiers left in Chinese order), location (在…上/里/中 frames), meaning (a word took the wrong one of its
           meanings), segmentation (the sentence was cut into the wrong words: look at the pieces), pronoun (the wrong
           form of address), name (a name not known or cut apart), other.
        5. Call novel_suggest_translations once with all of them (id, translation, note, kind). Change nothing else.

        Then answer with one short line: how many translations you suggested.
        """;

    internal static string Describe(string tool, int calls) => tool switch
    {
        "novel_translation_reports" => $"Claude Code is reading the reports… ({calls} steps)",
        "novel_names" => $"Claude Code is reading the novel's names… ({calls} steps)",
        "novel_suggest_translations" => $"Claude Code is writing its suggestions… ({calls} steps)",
        _ => $"Claude Code is working… ({calls} steps)",
    };

    public async Task<string> SuggestAsync(string bookId, string title, IReadOnlyList<string> reportIds, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            return await claude.RunAsync(AllowedTools, Instructions(bookId, title, reportIds), Describe, progress, Timeout, ct).ConfigureAwait(false);
        }
        catch (ClaudeCodeException ex)
        {
            throw new TranslationAgentException(ex.Message, ex);
        }
    }
}
