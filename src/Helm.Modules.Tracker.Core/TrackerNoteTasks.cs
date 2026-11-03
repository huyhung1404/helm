using Helm.Core.Ai;
using Helm.Core.Tasks;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tracker's to-do lists for Notes' "Make tasks" (<see cref="INoteTaskMaker"/>): AI reads the note and proposes its
/// tasks (<see cref="TaskProposals.FromNote"/>), and the ones the user keeps go to one list they pick.
/// </summary>
public sealed class TrackerNoteTasks(TrackerStore store, IAiText? ai, TimeProvider? time = null) : INoteTaskMaker
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string ModuleId => TrackerIds.ModuleId;
    public string Name => TrackerIds.DisplayName;
    public bool IsAvailable => ai is not null;

    public string? Where() => ai?.Where(TaskProposals.Feature);

    public IReadOnlyList<string> Lists()
    {
        var names = store.Workspaces().Where(w => w.Value.Kind == WorkspaceKind.Tasks).Select(w => w.Value.Name).ToList();
        return names.Count > 0 ? names : [TrackerTaskBridge.DefaultListName];
    }

    public async Task<NoteTaskAnswer> ProposeAsync(string title, string text, CancellationToken ct)
    {
        if (ai is null) return new NoteTaskAnswer([], "AI is not set up: AI → Proxy or Models & keys on the PC.");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), TimeZoneInfo.Local).DateTime);
        AiTextAnswer answer;
        try
        {
            answer = await Task.Run(() => ai.AskAsync(TaskProposals.FromNote(title, text, today), ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            answer = AiTextAnswer.Failed(ex.Message);
        }
        if (!answer.Ok) return new NoteTaskAnswer([], answer.Problem);
        var tasks = TaskProposals.Parse(answer.Text, TaskProposals.MaxTasks)
            .Select(t => new NoteTaskProposal(t.Title, t.Notes, t.Due, TaskProposals.DueDay(t.Due) is { } day ? TaskProposals.DayText(day) : "", t.Line))
            .ToList();
        return new NoteTaskAnswer(tasks, null, answer.RanOn);
    }

    public NoteTaskBatch Add(string listName, IReadOnlyList<NoteTaskProposal> tasks)
    {
        try
        {
            var list = TaskProposals.ListNamed(store, listName, out var made);
            var ids = TaskProposals.AddAll(store, list, null, tasks.Select(t => new ProposedTask(t.Title, t.Notes, t.Due, t.Line)));
            return new NoteTaskBatch(store.GetWorkspace(list)?.Name ?? listName, ids, made ? list : null);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public void Undo(NoteTaskBatch batch)
    {
        foreach (var id in batch.Ids) store.DeleteItem(id);
        if (batch.MadeList is { } list) store.DeleteWorkspace(list);
    }

    public bool? IsDone(string taskId) => store.GetItem(taskId) is { } item ? item.IsCompleted : null;

    public event EventHandler? TasksChanged
    {
        add => store.Changed += value;
        remove => store.Changed -= value;
    }
}
