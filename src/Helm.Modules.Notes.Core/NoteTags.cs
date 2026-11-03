using System.Text.RegularExpressions;
using Helm.Core.Text;

namespace Helm.Modules.Notes;

/// <summary>
/// Hashtags written in notes ("#idea #helm"): the ones in use, so an agent tags a new note the way the user already
/// does, and the line that adds them to a note.
/// </summary>
public static partial class NoteTags
{
    /// <summary>The tags of a text, folded (accents and case aside), without the '#'.</summary>
    public static IEnumerable<string> Of(string text) =>
        Tag().Matches(text).Select(m => TextSearch.Fold(m.Groups[1].Value)).Distinct(StringComparer.Ordinal);

    /// <summary>The tags of these notes, the most used first, with how many notes have each.</summary>
    public static IReadOnlyList<(string Tag, int Notes)> InUse(IEnumerable<NoteItem> notes, int max) =>
        [.. notes.SelectMany(n => Of(n.Title + "\n" + n.Body))
            .GroupBy(t => t, StringComparer.Ordinal)
            .Select(g => (Tag: g.Key, Notes: g.Count()))
            .OrderByDescending(t => t.Notes).ThenBy(t => t.Tag, StringComparer.Ordinal)
            .Take(max)];

    /// <summary>The text with a last line of the tags it does not have yet ("#idea #game"); unchanged when none.</summary>
    public static string Append(string text, IEnumerable<string> tags)
    {
        var have = Of(text).ToHashSet(StringComparer.Ordinal);
        var add = tags.Select(Clean).Where(t => t.Length > 0 && have.Add(TextSearch.Fold(t))).ToList();
        if (add.Count == 0) return text;
        var line = string.Join(' ', add.Select(t => "#" + t));
        return text.Length == 0 ? line : text.TrimEnd('\n') + "\n\n" + line;
    }

    /// <summary>"#Game dev" → "game-dev": a tag is one word, lower case.</summary>
    private static string Clean(string tag) =>
        string.Join('-', tag.Trim().TrimStart('#').ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-')]))
            .Where(w => w.Length > 0));

    [GeneratedRegex(@"(?<![\p{L}\p{N}&/])#(\p{L}[\p{L}\p{N}\p{Mn}_-]*)")]
    private static partial Regex Tag();
}
