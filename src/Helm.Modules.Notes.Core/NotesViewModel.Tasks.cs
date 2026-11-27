using Helm.Core.Tasks;

namespace Helm.Modules.Notes;

// The lines of a note that became tasks (Notes' "Make tasks" before 0.96) keep showing whether each task is done.
public sealed partial class NotesViewModel
{
    private readonly ITaskBridge? _tasks;

    /// <summary>
    /// The open note's lines that became tasks show whether each is done: " → Tracker ✓" once done, " → Tracker" again
    /// when reopened. An edit like typing, so it saves and syncs as one.
    /// </summary>
    private void MarkDoneLines()
    {
        if (_tasks is null || !IsEditorOpen || EditTrashed || _editorId is not { } id
            || _store.Get(id) is not { TaskLinks.Count: > 0 } note) return;
        var marked = NoteTaskLines.MarkDone(EditBody, note.TaskLinks, NoteTaskLines.Tag(_tasks.Name), _tasks.IsDone);
        if (marked != EditBody) EditBody = marked;
    }
}
