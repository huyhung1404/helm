using Helm.Core.Text;

namespace Helm.Modules.Notes;

/// <summary>
/// The lines of a note against the tasks AI found in it ("Make tasks"): which line each task came from, the lines it
/// left out, and the small "→ Tracker" tag put at the end of a line that became a task. The tag is plain text, so
/// every device and older version shows it, and taking the tasks back removes it again; nothing else of the note is
/// touched.
/// </summary>
public static class NoteTaskLines
{
    private const int MinMatch = 4;
    private const int MaxQuoted = 3;
    private const int MaxQuote = 40;

    /// <summary>The tag a line gets: " → Tracker".</summary>
    public static string Tag(string toolName) => $" → {toolName}";

    /// <summary>
    /// For each task, the index of the note's line it came from (or -1): the line AI quoted, else one holding the task's
    /// title; each line serves one task. Lines already tagged are never matched again.
    /// </summary>
    public static IReadOnlyList<int> Match(string text, IReadOnlyList<(string Title, string? Line)> tasks, string tag)
    {
        var lines = Split(text);
        var keys = lines.Select(l => l.EndsWith(tag, StringComparison.Ordinal) ? "" : Key(l)).ToList();
        var used = new HashSet<int>();
        var found = new List<int>();
        foreach (var (title, line) in tasks)
        {
            var at = Find(keys, used, line is null ? "" : Key(line), exact: true);
            if (at < 0 && line is not null) at = Find(keys, used, Key(line), exact: false);
            if (at < 0) at = Find(keys, used, Key(title), exact: false);
            if (at >= 0) used.Add(at);
            found.Add(at);
        }
        return found;
    }

    /// <summary>
    /// The note's lines no task came from (blank ones and tagged ones aside), said the way the panel says them:
    /// "Skipped “Tuần sau về quê”: not a task."; "" when every line became a task.
    /// </summary>
    public static string Skipped(string text, IEnumerable<int> matched, string tag)
    {
        var used = matched.ToHashSet();
        var left = Split(text)
            .Select((line, i) => (Line: line.Trim(), i))
            .Where(l => !used.Contains(l.i) && Key(l.Line).Length > 0 && !l.Line.EndsWith(tag.Trim(), StringComparison.Ordinal))
            .Select(l => Quote(Strip(l.Line)))
            .ToList();
        if (left.Count == 0) return "";
        var shown = string.Join(", ", left.Take(MaxQuoted).Select(q => $"“{q}”"));
        var more = left.Count > MaxQuoted ? $" and {left.Count - MaxQuoted} more" : "";
        return left.Count == 1 ? $"Skipped {shown}: not a task." : $"Skipped {shown}{more}: not tasks.";
    }

    /// <summary>The text with <paramref name="tag"/> at the end of each line given (once); its other lines as they were.</summary>
    public static string AddTags(string text, IEnumerable<int> lineIndexes, string tag)
    {
        var lines = Split(text);
        foreach (var i in lineIndexes.Where(i => i >= 0 && i < lines.Length).Distinct())
            if (!lines[i].EndsWith(tag, StringComparison.Ordinal)) lines[i] = lines[i].TrimEnd() + tag;
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The text with the tag taken off <paramref name="taggedLines"/> (the lines as <see cref="AddTags"/> left them),
    /// wherever they are now; lines edited since keep theirs.
    /// </summary>
    public static string RemoveTags(string text, IEnumerable<string> taggedLines, string tag)
    {
        var lines = Split(text);
        foreach (var tagged in taggedLines)
        {
            var at = Array.IndexOf(lines, tagged);
            if (at >= 0 && tagged.EndsWith(tag, StringComparison.Ordinal)) lines[at] = tagged[..^tag.Length];
        }
        return string.Join('\n', lines);
    }

    /// <summary>The lines of a note's text (Windows line ends read as one).</summary>
    public static string[] Split(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static int Find(List<string> keys, HashSet<int> used, string key, bool exact)
    {
        if (key.Length == 0 || (!exact && key.Length < MinMatch)) return -1;
        for (var i = 0; i < keys.Count; i++)
        {
            if (used.Contains(i) || keys[i].Length == 0) continue;
            if (exact ? keys[i] == key : keys[i].Contains(key, StringComparison.Ordinal) || (keys[i].Length >= MinMatch && key.Contains(keys[i], StringComparison.Ordinal)))
                return i;
        }
        return -1;
    }

    /// <summary>A line to compare: no list mark or check box, accents and case aside, spaces folded.</summary>
    private static string Key(string line) =>
        string.Join(' ', TextSearch.Fold(Strip(line)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>"- [ ] Buy milk" → "Buy milk".</summary>
    private static string Strip(string line)
    {
        var s = line.Trim();
        while (true)
        {
            var before = s;
            if (s.StartsWith("[ ]", StringComparison.Ordinal) || s.StartsWith("[x]", StringComparison.OrdinalIgnoreCase)) s = s[3..].TrimStart();
            else if (s.Length > 0 && s[0] is '-' or '*' or '+' or '•' or '·' or '–' or '☐' or '☑' or '✓') s = s[1..].TrimStart();
            else if (s.Length > 1 && char.IsAsciiDigit(s[0]) && s.IndexOfAny(['.', ')']) is var mark and > 0 and < 4 && s[..mark].All(char.IsAsciiDigit))
                s = s[(mark + 1)..].TrimStart();
            if (s == before) return s;
        }
    }

    private static string Quote(string line) => line.Length <= MaxQuote ? line : line[..MaxQuote].TrimEnd() + "…";
}
