namespace Helm.Core.Tasks;

/// <summary>A task AI found in a note, for the user to keep or drop.</summary>
/// <param name="Due">The due day or time as the to-do list takes it (2026-11-06, 2026-11-06T09:30); null for none.</param>
/// <param name="DueText">The due day as shown beside it ("Fri 6 Nov"); "" for none.</param>
/// <param name="Line">The note's line it came from, as written there; null when AI did not say.</param>
public sealed record NoteTaskProposal(string Title, string Notes, string? Due, string DueText, string? Line);

/// <param name="Problem">Why there are no proposals (no AI, the call failed); null when it worked.</param>
/// <param name="RanOn">The agent and Helm PC that answered; null when the API did.</param>
public sealed record NoteTaskAnswer(IReadOnlyList<NoteTaskProposal> Tasks, string? Problem, string? RanOn = null);

/// <summary>What one "Add" did, so it can be taken back.</summary>
/// <param name="Ids">The tasks made, in the order of the proposals added.</param>
/// <param name="MadeList">The list made for it (the user had none by that name), removed again by Undo.</param>
public sealed record NoteTaskBatch(string ListName, IReadOnlyList<string> Ids, string? MadeList);

/// <summary>
/// Another tool's to-do lists, for Notes' "Make tasks" (AI reads a note and proposes its tasks) without Notes
/// referencing that tool. Registered by the tool that keeps the tasks, like <see cref="ITaskBridge"/>; callers use it
/// only while <see cref="ModuleId"/> is on. UI thread, except <see cref="ProposeAsync"/>, which may be awaited there.
/// </summary>
public interface INoteTaskMaker
{
    /// <summary>The tool that keeps the tasks, e.g. "tracker".</summary>
    string ModuleId { get; }

    /// <summary>What the note's tags call it, e.g. "Tracker".</summary>
    string Name { get; }

    /// <summary>AI is set up on this device: "Make tasks" is offered.</summary>
    bool IsAvailable { get; }

    /// <summary>Where the call would run now ("Haiku 5.5 · agent on HUNG-PC · 0đ"), or null.</summary>
    string? Where();

    /// <summary>The to-do lists by name, the one tasks go to by default first.</summary>
    IReadOnlyList<string> Lists();

    /// <summary>Asks AI for the note's tasks. Never throws for the AI's problems: they come back in the answer.</summary>
    Task<NoteTaskAnswer> ProposeAsync(string title, string text, CancellationToken ct);

    /// <summary>Adds the tasks to the list named (made when there is none by that name).</summary>
    /// <exception cref="InvalidOperationException">The list could not be used or made.</exception>
    NoteTaskBatch Add(string listName, IReadOnlyList<NoteTaskProposal> tasks);

    /// <summary>Takes a batch back: its tasks, and the list when <see cref="Add"/> made it.</summary>
    void Undo(NoteTaskBatch batch);

    /// <summary>Whether a task <see cref="Add"/> made is done now; null when it is gone.</summary>
    bool? IsDone(string taskId);

    /// <summary>The tasks changed, here or on another device (any thread): a note's done lines may change their mark.</summary>
    event EventHandler? TasksChanged;
}
