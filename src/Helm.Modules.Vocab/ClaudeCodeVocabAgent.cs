using Helm.Core.Mcp;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary's agent on Windows: Claude Code without a window (<see cref="ClaudeCodeRunner"/>), allowed only the
/// vocabulary tools. What it saves arrives in the list while it works, and syncs to the phone.
/// </summary>
public sealed class ClaudeCodeVocabAgent(ClaudeCodeRunner claude) : IVocabAgent
{
    /// <summary>A handful of words take a few minutes.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    /// <summary>Words broken down in one run; the rest wait for the next.</summary>
    public const int WordsPerRun = 12;

    /// <summary>The only tools it may call.</summary>
    public static IReadOnlyList<string> AllowedTools { get; } = ClaudeCodeRunner.ToolNames(
        "vocab_pending", "vocab_find_parts", "vocab_save_analysis", "vocab_save_analyses", "vocab_get", "vocab_words", "vocab_add_words");

    public string? Unavailable() => claude.Unavailable();

    /// <summary>What Claude Code is asked to do for the waiting words (in English, the language it works best in).</summary>
    internal static string AnalyzeInstructions =>
        $"""
        Break down the words the user is learning in Helm's Vocabulary, using only Helm's vocabulary tools.

        1. Call vocab_pending with limit {WordsPerRun}. Read the guide in the tools' descriptions and follow it exactly.
           A word with a redo_note was sent back by the user: fix what the note says.
        2. For every piece you will use, call vocab_find_parts (all forms of a language in one call) and reuse the id
           of the same piece with the same meaning.
        3. Save all the words with one vocab_save_analyses call. If some come back with something to fix, fix them and
           save those again.

        Then answer with one short line in Vietnamese: how many words you broke down.
        """;

    /// <summary>What Claude Code is asked to do for a family.</summary>
    internal static string SuggestInstructions(string partId, string form, string meaning, VocabLanguage language) =>
        $"""
        The user learns words by their parts in Helm's Vocabulary and wants more words of one family. Use only Helm's
        vocabulary tools.

        The part: "{form}" (id {partId}), meaning "{meaning}", in {(language == VocabLanguage.Chinese ? "Chinese" : "English")}.

        1. Call vocab_words with query "{form}" and limit 50 to see the words the user already has.
        2. Pick 3 common, useful words that contain this part with this meaning and that the user does not have yet
           ({(language == VocabLanguage.Chinese ? "HSK 1-4 words first" : "words at CEFR A2-B2 first, the user is preparing for Aptis B1")}).
        3. Add them with vocab_add_words (note "Cùng gốc {form}").
        4. Break them down as the guide in the tools' descriptions says: vocab_find_parts, then one vocab_save_analyses
           call, reusing part id {partId} for this part.

        Then answer with one short line in Vietnamese: which words you added.
        """;

    internal static string Describe(string tool, int calls) => tool switch
    {
        "vocab_pending" => $"Claude Code is reading the new words… ({calls} steps)",
        "vocab_find_parts" => $"Claude Code is looking up their parts… ({calls} steps)",
        "vocab_save_analysis" or "vocab_save_analyses" => $"Claude Code is saving the analyses… ({calls} steps)",
        "vocab_add_words" => $"Claude Code is adding words… ({calls} steps)",
        _ => $"Claude Code is working… ({calls} steps)",
    };

    public Task<string> AnalyzeAsync(IProgress<string>? progress, CancellationToken ct) => RunAsync(AnalyzeInstructions, progress, ct);

    public Task<string> SuggestAsync(string partId, string form, string meaning, VocabLanguage language, IProgress<string>? progress, CancellationToken ct) =>
        RunAsync(SuggestInstructions(partId, form, meaning, language), progress, ct);

    private async Task<string> RunAsync(string instructions, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            return await claude.RunAsync(AllowedTools, instructions, Describe, progress, Timeout, ct).ConfigureAwait(false);
        }
        catch (ClaudeCodeException ex)
        {
            throw new VocabAgentException(ex.Message, ex);
        }
    }
}
