using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Ai;

namespace Helm.Modules.Tracker;

/// <summary>A proposed task or step, ticked to be added.</summary>
public sealed partial class TaskProposalChoice(ProposedTask task) : ObservableObject
{
    public ProposedTask Task { get; } = task;

    [ObservableProperty] private bool _keep = true;
}

/// <summary>
/// AI's proposals for the to-do lists, for the user to pick from: the steps of a big task ("Break into steps" on a
/// task), or the tasks in a note ("Make tasks" on a note, into a list the user names). Only the ticked ones are added,
/// and the whole batch can be taken back. UI thread only; the AI call runs off it.
/// </summary>
public sealed partial class TrackerProposalViewModel(TrackerStore store, IAiText? ai, TimeProvider? time = null) : ObservableObject
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private string? _parentId;
    private IReadOnlyList<string> _added = [];
    private string? _madeList;
    private int _asked;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddCommand))] private bool _isBusy;

    /// <summary>What the proposals are for: the task's title, or the note's.</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>For a note: the list the tasks go to (made when the user has none by that name).</summary>
    [ObservableProperty] private string _listName = "";

    [ObservableProperty] private string _status = "";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UndoCommand))] private bool _canUndo;

    /// <summary>A task's steps (subtasks), not a note's tasks.</summary>
    public bool IsSteps => _parentId is not null;

    /// <summary>AI is set up: the commands that open this are offered.</summary>
    public bool IsAvailable => ai is not null;

    public ObservableCollection<TaskProposalChoice> Choices { get; } = [];

    /// <summary>Proposes steps for a task (not a subtask: subtasks have none).</summary>
    public Task ForTaskAsync(string taskId)
    {
        if (store.GetItem(taskId) is not { IsSubtask: false } task) return Task.CompletedTask;
        Open(task.Title, taskId, "");
        var existing = store.Subtasks(taskId).Select(s => s.Value.Title).ToList();
        return ProposeAsync(TaskProposals.Steps(task.Title, task.Notes, existing, Today()), TaskProposals.MaxSteps);
    }

    /// <summary>Proposes the tasks in a note, for <paramref name="listName"/> (default: the first to-do list).</summary>
    public Task ForNoteAsync(string noteTitle, string noteText, string? listName = null)
    {
        Open(noteTitle, null, listName?.Trim() is { Length: > 0 } name ? name
            : store.Workspaces().FirstOrDefault(w => w.Value.Kind == WorkspaceKind.Tasks)?.Value.Name ?? TrackerTaskBridge.DefaultListName);
        return ProposeAsync(TaskProposals.FromNote(noteTitle, noteText, Today()), TaskProposals.MaxTasks);
    }

    private void Open(string title, string? parentId, string listName)
    {
        _asked++;
        _parentId = parentId;
        Title = title;
        ListName = listName;
        Choices.Clear();
        CanUndo = false;
        _added = [];
        _madeList = null;
        OnPropertyChanged(nameof(IsSteps));
        IsOpen = true;
    }

    private async Task ProposeAsync(AiTextRequest request, int max)
    {
        if (ai is null)
        {
            Status = "AI is not set up: AI → Proxy or Models & keys on the PC.";
            return;
        }
        var asked = _asked;
        IsBusy = true;
        Status = "Asking AI…";
        AiTextAnswer answer;
        try
        {
            answer = await System.Threading.Tasks.Task.Run(() => ai.AskAsync(request, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            answer = AiTextAnswer.Failed(ex.Message);
        }
        // Opened again for something else meanwhile: this answer is stale.
        if (asked != _asked) return;
        IsBusy = false;
        if (!answer.Ok)
        {
            Status = answer.Problem!;
            return;
        }
        var proposals = TaskProposals.Parse(answer.Text, max);
        foreach (var p in proposals)
        {
            var choice = new TaskProposalChoice(p);
            choice.PropertyChanged += (_, _) => AddCommand.NotifyCanExecuteChanged();
            Choices.Add(choice);
        }
        Status = proposals.Count == 0 ? "AI found nothing to add." : answer.RanOn is { } pc ? $"Untick what you do not want · {pc}" : "Untick what you do not want";
        AddCommand.NotifyCanExecuteChanged();
    }

    private bool CanAdd() => !IsBusy && !CanUndo && Choices.Any(c => c.Keep);

    /// <summary>Adds the ticked ones: as the task's subtasks, or as tasks of the named list.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var kept = Choices.Where(c => c.Keep).Select(c => c.Task).ToList();
        if (kept.Count == 0) return;
        try
        {
            string listId;
            if (_parentId is { } parent)
            {
                listId = store.GetItem(parent)?.WorkspaceId ?? throw new InvalidOperationException("The task is gone.");
            }
            else
            {
                var name = ListName.Trim().Length > 0 ? ListName.Trim() : TrackerTaskBridge.DefaultListName;
                listId = store.Workspaces().FirstOrDefault(w => w.Value.Kind == WorkspaceKind.Tasks
                    && string.Equals(w.Value.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase))?.Id ?? (_madeList = store.AddWorkspace(name));
            }
            _added = TaskProposals.AddAll(store, listId, _parentId, kept);
            CanUndo = true;
            Status = _parentId is not null ? $"Added {_added.Count} steps." : $"Added {_added.Count} tasks to {store.GetWorkspace(listId)?.Name}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Status = ex.Message;
        }
        AddCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Takes the added batch back (and the list, when this made it).</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        foreach (var id in _added) store.DeleteItem(id);
        if (_madeList is { } list) store.DeleteWorkspace(list);
        _added = [];
        _madeList = null;
        CanUndo = false;
        Status = "Taken back.";
        AddCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Close()
    {
        _asked++;
        IsBusy = false;
        IsOpen = false;
    }

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), TimeZoneInfo.Local).DateTime);
}
