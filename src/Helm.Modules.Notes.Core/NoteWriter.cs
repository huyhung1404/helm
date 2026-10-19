using Helm.Core.Capture;

namespace Helm.Modules.Notes;

/// <summary>Notes for other tools (Scratch's Send to Notes): the text becomes the body of a new note, untitled.</summary>
public sealed class NoteWriter(NotesStore store) : INoteWriter
{
    public string ModuleId => NotesIds.ModuleId;

    public (string Id, string Title) Write(string text)
    {
        var id = store.Add("", text);
        return (id, store.Get(id)?.DisplayTitle ?? NoteText.Untitled);
    }
}
