using Helm.Modules.NovelReader.Names;

namespace Helm.Modules.NovelReader;

/// <summary>
/// The AI name scan on Windows: Claude Code (<see cref="ClaudeCodeRunner"/>), allowed only Novel Reader's name tools. It
/// reads the candidates through Helm's MCP server and adds the names it judges real with <c>novel_add_names</c>, so they
/// arrive in the library while it works.
/// </summary>
public sealed class ClaudeCodeNameAgent(ClaudeCodeRunner claude) : INameScanAgent
{
    /// <summary>A long novel has a few hundred candidates; far more than this means it is stuck.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    /// <summary>The only tools it may call.</summary>
    public static IReadOnlyList<string> AllowedTools { get; } =
        ClaudeCodeRunner.ToolNames("novel_list", "novel_name_candidates", "novel_names", "novel_add_names", "novel_ignore_names");

    public string Name => "Claude Code";

    public string? Unavailable() => claude.Unavailable();

    /// <summary>What Claude Code is asked to do (in English, the language it works best in).</summary>
    public static string Instructions(string bookId, string title) =>
        $"""
        Find the character names of the novel "{title}" (id {bookId}) in Helm's Novel Reader, using only Helm's novel tools.
        The reader reads Chinese web novels through a Vietnamese QuickTranslator converter, which does not know this
        novel's names, so they come out as ordinary words.

        1. Call novel_name_candidates with novel "{bookId}" and offset 0, then keep calling it with the returned
           next_offset until next_offset is null. Each candidate has its count, Hán Việt reading, what Helm's logic scan
           thinks, whether it is saved already, and sentences where it is used.
        2. Judge every candidate that is not saved or set aside from its sentences, not from its characters alone:
           - a name is a person (full name, given name, courtesy name, nickname used as a name), a named place, or a named
             sect, clan or house (林府, 洛家). A given name that also appears inside a full name (景桓 of 洛景桓) is still a
             name when the novel uses it alone: add it too;
           - not a name: a piece of a longer word or idiom (成怒 of 恼羞成怒), a name with a word stuck to it (向林宛,
             谢珩低), a title or form of address (侯爷, 皇上), an ordinary word.
        3. After each page, call novel_add_names with the names of that page, written as a Vietnamese reader writes them:
           the Hán Việt reading, every syllable capitalised for a person or a place (林宛 → Lâm Uyển); a house or family
           word stays lowercase (林府 → Lâm phủ, 洛家 → Lạc gia). Then call novel_ignore_names with the words of that page
           that are clearly not names (later scans skip them for good, so never a possible name). Leave out words you are
           unsure of.
        4. Never change or remove names that are saved already.

        When every page is done, answer with one short line: how many names you added and how many words you set aside.
        """;

    /// <summary>What the scan says while Claude Code calls a tool.</summary>
    internal static string Describe(string tool, int calls) => tool switch
    {
        "novel_name_candidates" => $"Claude Code is reading the possible names… ({calls} steps)",
        "novel_add_names" => $"Claude Code is adding names… ({calls} steps)",
        "novel_ignore_names" => $"Claude Code is setting aside words that are not names… ({calls} steps)",
        _ => $"Claude Code is working… ({calls} steps)",
    };

    public async Task<string> FindNamesAsync(string bookId, string title, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            return await claude.RunAsync(AllowedTools, Instructions(bookId, title), Describe, progress, Timeout, ct).ConfigureAwait(false);
        }
        catch (ClaudeCodeException ex)
        {
            throw new NameAgentException(ex.Message, ex);
        }
    }
}
