namespace Helm.Modules.Notes;

/// <summary>
/// The small "→ Tracker" tag at the end of a note's line that became a task (Notes' "Make tasks" before 0.96), which
/// becomes "→ Tracker ✓" once that task is done (<see cref="MarkDone"/>). The tag is plain text, so every device and
/// older version shows it; nothing else of the note is touched.
/// </summary>
public static class NoteTaskLines
{
    /// <summary>What follows the tag once the line's task is done: " → Tracker ✓".</summary>
    public const string DoneMark = " ✓";

    /// <summary>The tag a line has: " → Tracker".</summary>
    public static string Tag(string toolName) => $" → {toolName}";

    /// <summary>
    /// The text with each linked line's tag showing its task: " → Tracker ✓" when done, " → Tracker" when open again.
    /// A line edited since (or a task that is gone) keeps its tag as it is.
    /// </summary>
    public static string MarkDone(string text, IEnumerable<NoteTaskLink> links, string tag, Func<string, bool?> isDone)
    {
        var lines = Split(text);
        var changed = false;
        foreach (var link in links)
        {
            if (isDone(link.TaskId) is not { } done) continue;
            var open = link.Line + tag;
            var at = Array.IndexOf(lines, done ? open : open + DoneMark);
            if (at < 0) continue;
            lines[at] = done ? open + DoneMark : open;
            changed = true;
        }
        return changed ? string.Join('\n', lines) : text;
    }

    /// <summary>The lines of a note's text (Windows line ends read as one).</summary>
    public static string[] Split(string text) => text.Replace("\r\n", "\n").Split('\n');
}
