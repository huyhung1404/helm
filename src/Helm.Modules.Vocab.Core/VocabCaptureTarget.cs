using System.Text.RegularExpressions;
using Helm.Core.Capture;

namespace Helm.Modules.Vocab;

/// <summary>
/// Quick Capture, Android's Share → "Save to Helm" and the phone's text selection ("Add word") for Vocabulary: words
/// to learn, added to the list that waits for an AI agent. Several words go in at once, separated by commas;
/// "| where you met it" adds a note to each. A single word shared from another app picks this target by itself; "/v"
/// picks it by hand.
/// </summary>
public sealed partial class VocabCaptureTarget(VocabStore store) : ICaptureTarget
{
    public const string TargetId = "vocab";

    /// <summary>At most this many words in one capture (a pasted paragraph is not a list of words).</summary>
    public const int MaxWords = 20;

    public string Id => TargetId;
    public string ModuleId => VocabIds.ModuleId;
    public string Name => "Word";
    public string Prefix => "v";
    public string Example => "relative, 补救 | where you met them: words to learn, broken down by an AI agent.";
    // After the others, so their Ctrl+1 … Ctrl+6 stay where they were.
    public int Order => 70;

    /// <summary>
    /// A lone English word, or a few Chinese characters, is most likely one to learn. Short phrases are left to the
    /// other targets ("buy milk tomorrow" is a task); "/v" still takes them.
    /// </summary>
    public bool Claims(string text)
    {
        var t = text.Trim();
        if (t.Length is 0 or > 30) return false;
        return VocabText.LanguageOf(t) == VocabLanguage.Chinese ? t.All(VocabText.IsHan) && VocabText.Length(t) <= 8 : EnglishWord().IsMatch(t);
    }

    public CapturePreview Preview(string text)
    {
        var (words, note) = Parse(text);
        if (words.Count == 0) return new(false, Example);
        if (words.Count > MaxWords) return new(false, $"At most {MaxWords} words at once.");
        var known = words.Where(w => store.FindWord(w) is not null).ToList();
        var fresh = words.Except(known).ToList();
        if (fresh.Count == 0) return new(false, known.Count == 1 ? $"“{known[0]}” is already in your words." : "These are all in your words already.");
        var line = fresh.Count == 1 ? $"Word to learn: “{fresh[0]}”" : $"{fresh.Count} words to learn: {string.Join(", ", fresh)}";
        if (known.Count > 0) line += $" ({string.Join(", ", known)} already there)";
        if (note.Length > 0) line += $" · note “{note}”";
        return new(true, line + " · waits for an AI agent");
    }

    public CaptureResult Capture(string text)
    {
        var (words, note) = Parse(text);
        if (words.Count == 0) return new(false, "Type a word first.");
        if (words.Count > MaxWords) return new(false, $"At most {MaxWords} words at once.");
        var added = new List<string>();
        try
        {
            foreach (var word in words)
                if (store.AddWord(word, note).Added) added.Add(word);
        }
        catch (ArgumentException ex)
        {
            return new(false, ex.Message);
        }
        return added.Count switch
        {
            0 => new(false, words.Count == 1 ? $"“{words[0]}” is already in your words." : "These are all in your words already."),
            1 => new(true, $"Added “{added[0]}” to Vocabulary; it waits for an AI agent to break it down."),
            _ => new(true, $"Added {added.Count} words to Vocabulary: {string.Join(", ", added)}."),
        };
    }

    /// <summary>
    /// The words (tidied, each once) and the note: "relative, translate | Aptis p.4" → [relative, translate], "Aptis p.4".
    /// A second line is a note too.
    /// </summary>
    internal static (IReadOnlyList<string> Words, string Note) Parse(string text)
    {
        var cut = text.IndexOfAny(['|', '\n']);
        var head = cut < 0 ? text : text[..cut];
        var note = cut < 0 ? "" : string.Join(' ', text[(cut + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var words = head.Split([',', '，', ';', '；', '、'], StringSplitOptions.RemoveEmptyEntries)
            .Select(VocabText.Tidy)
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (words, note);
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z'’\-]*$")]
    private static partial Regex EnglishWord();
}
