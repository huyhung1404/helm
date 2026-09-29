using Helm.Core.Settings;

namespace Helm.Modules.Notes;

/// <summary>
/// Device-local preferences (the notes themselves are synced through <see cref="NotesStore"/>). Stored in
/// settings/notes.json on both apps.
/// </summary>
public sealed class NotesSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public const int MinFontSize = 11;
    public const int MaxFontSize = 24;

    public int Version { get; set; }

    /// <summary>The note the page opens on (Windows); null opens none.</summary>
    public string? SelectedNoteId { get; set; }

    public NotesSortOrder SortOrder { get; set; } = NotesSortOrder.Edited;

    /// <summary>Editor text size in points (Windows) or dp (Android).</summary>
    public int EditorFontSize { get; set; } = 14;

    /// <summary>A fixed-width font in the editor, for code and lists that line up.</summary>
    public bool Monospace { get; set; }
}
