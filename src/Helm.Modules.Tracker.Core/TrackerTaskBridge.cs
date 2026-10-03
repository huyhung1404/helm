using Helm.Core.Tasks;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tracker's to-do lists for other tools (<see cref="ITaskBridge"/>): Missions sends a step here as a task with a due
/// day, and finishing either one finishes the other. Tasks go to the first to-do list; one is made when there is none.
/// </summary>
public sealed class TrackerTaskBridge : ITaskBridge
{
    /// <summary>The list made when the user has no to-do list yet.</summary>
    public const string DefaultListName = "To-do";

    private readonly TrackerStore _store;

    public TrackerTaskBridge(TrackerStore store)
    {
        _store = store;
        _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string ModuleId => TrackerIds.ModuleId;
    public string Name => TrackerIds.DisplayName;
    public event EventHandler? Changed;

    public string? Add(string title, string notes, DateOnly? due)
    {
        try
        {
            var list = _store.Workspaces().FirstOrDefault(w => w.Value.Kind == WorkspaceKind.Tasks)?.Id
                ?? _store.AddWorkspace(DefaultListName, WorkspaceKind.Tasks);
            return _store.AddItem(list, new TrackerItemDraft(title, DueDate: due, Notes: notes));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public bool? IsDone(string id) => _store.GetItem(id) is { } item ? item.IsCompleted : null;

    public bool Complete(string id) => _store.Complete(id);
}
