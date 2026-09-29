using System.Text.Json.Serialization;

namespace Helm.Modules.Notes;

public static class NotesIds
{
    /// <summary>Module id on both apps: the settings file (notes.json) and the enabled-state key.</summary>
    public const string ModuleId = "notes";

    public const string DisplayName = "Notes";

    public const string Description = "Write notes that sync across your devices, end-to-end encrypted. When two devices edit the same note, both versions are kept.";
}

/// <summary>One note (synced record in <c>notes.items</c>).</summary>
public sealed record NoteItem
{
    /// <summary>
    /// The most a note can hold. A synced record is at most 1 MiB once encrypted; JSON may write a Vietnamese letter as
    /// six bytes, so 100,000 characters stay well inside it.
    /// </summary>
    public const int MaxLength = 100_000;

    /// <summary>May be empty: the list then shows the first line of <see cref="Body"/>.</summary>
    public string Title { get; init; } = "";

    public string Body { get; init; } = "";

    /// <summary>Listed at the top.</summary>
    public bool Pinned { get; init; }

    /// <summary>In the trash: hidden from the list, restorable, deleted for good after <see cref="NotesStore.TrashDays"/> days.</summary>
    public bool Trashed { get; init; }

    public DateTimeOffset? TrashedAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the text last changed (not when it was pinned or trashed).</summary>
    public DateTimeOffset EditedAt { get; init; }

    /// <summary>
    /// A new random value on every save of the text. The editor remembers the revision it opened, so a save over a
    /// version that meanwhile arrived from another device is kept as a copy instead of overwriting it.
    /// </summary>
    public string Rev { get; init; } = "";

    /// <summary>The title, or else the first line of the text, or else "Untitled".</summary>
    [JsonIgnore]
    public string DisplayTitle => NoteText.DisplayTitle(Title, Body);
}

/// <summary>Text helpers for notes, shared by the store, the pages and the capture target.</summary>
public static class NoteText
{
    public const string Untitled = "Untitled";

    public static string DisplayTitle(string title, string body)
    {
        var t = title.Trim();
        if (t.Length > 0) return t;
        var first = FirstLine(body);
        return first.Length > 0 ? Shorten(first, 80) : Untitled;
    }

    /// <summary>The first non-empty line of the text, trimmed.</summary>
    public static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return "";
    }

    /// <summary>
    /// A one-line preview of the text for the list: the lines after the one used as the title (or all of them when
    /// there is a title), joined with spaces.
    /// </summary>
    public static string Preview(string title, string body, int max = 140)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        if (title.Trim().Length == 0) lines = lines.Skip(1);
        return Shorten(string.Join(' ', lines), max);
    }

    public static int Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>Line endings as \n, so notes from Windows and Android compare equal.</summary>
    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>A file name for exporting a note, without characters Windows forbids.</summary>
    public static string FileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var name = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) name = Untitled;
        return Shorten(name, 80).Replace("…", "");
    }
}

public enum NotesSortOrder
{
    /// <summary>Most recently edited first.</summary>
    Edited,

    /// <summary>Newest first.</summary>
    Created,

    /// <summary>A to Z.</summary>
    Title,
}

/// <summary>What happened to a save from the editor.</summary>
public enum NoteSaveOutcome
{
    /// <summary>The note now has the text.</summary>
    Saved,

    /// <summary>Nothing changed.</summary>
    Unchanged,

    /// <summary>
    /// The note changed on another device since the editor opened it: the other version stays in the note and the
    /// editor's text became a new note (<see cref="NoteSaveResult.CopyId"/>).
    /// </summary>
    SavedAsCopy,

    /// <summary>The note was deleted for good elsewhere; the text was saved as a new note.</summary>
    Recreated,
}

public sealed record NoteSaveResult(NoteSaveOutcome Outcome, string Id, string Rev, string? CopyId = null);
