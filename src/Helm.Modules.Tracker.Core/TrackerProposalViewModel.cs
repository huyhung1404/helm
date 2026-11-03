using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Ai;

namespace Helm.Modules.Tracker;

/// <summary>A proposed task or step, ticked to be added; its title can be changed before.</summary>
public sealed partial class TaskProposalChoice(ProposedTask task) : ObservableObject
{
    public ProposedTask Task { get; } = task;

    [ObservableProperty] private bool _keep = true;

    [ObservableProperty] private string _title = task.Title;

    /// <summary>The due day it gets ("Sat 14 Nov"), or "" for none.</summary>
    [ObservableProperty] private string _dueText = "";

    public bool HasDue => DueText.Length > 0;

    partial void OnDueTextChanged(string value) => OnPropertyChanged(nameof(HasDue));

    /// <summary>The day it is added with (<see cref="ProposedTask.Due"/> unless the steps' days are spread).</summary>
    internal string? Due { get; set; } = task.Due;
}

/// <summary>
/// AI's proposals for the to-do lists, for the user to pick from: the steps of a big task ("Split into steps" on a
/// task: they become its checklist, its subtasks), or the tasks in a note (into a list the user names). Only the ticked
/// ones are added, with their titles as edited, and the whole batch can be taken back. A task's steps may get due days
/// spread before the task's own. UI thread only; the AI call runs off it.
/// </summary>
public sealed partial class TrackerProposalViewModel(TrackerStore store, IAiText? ai, TimeProvider? time = null) : ObservableObject
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IReadOnlyList<string> _added = [];
    private string? _madeList;
    private int _asked;
    private Func<Task>? _again;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsProposing))] [NotifyPropertyChangedFor(nameof(IsStuck))] private bool _isOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyPropertyChangedFor(nameof(IsProposing))] [NotifyPropertyChangedFor(nameof(IsStuck))]
    private bool _isBusy;

    /// <summary>The task the steps are for; null for a note's tasks.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSteps))] private string? _taskId;

    /// <summary>What the proposals are for: the task's title, or the note's.</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>For a note: the list the tasks go to (made when the user has none by that name).</summary>
    [ObservableProperty] private string _listName = "";

    /// <summary>While AI works, where it runs ("Sonnet 5.5 · agent on HUNG-PC · 0đ"); then what to do, or a problem.</summary>
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyPropertyChangedFor(nameof(IsProposing))] [NotifyPropertyChangedFor(nameof(IsStuck))]
    private bool _canUndo;

    /// <summary>The task's due day: its steps' days can be spread before it. Null when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSpread))]
    [NotifyPropertyChangedFor(nameof(SpreadText))]
    private DateOnly? _taskDue;

    /// <summary>"Spread due dates before …": on by default when the task has a due date.</summary>
    [ObservableProperty] private bool _spreadDates;

    /// <summary>A task's steps (subtasks), not a note's tasks.</summary>
    public bool IsSteps => TaskId is not null;

    /// <summary>AI is set up: the commands that open this are offered.</summary>
    public bool IsAvailable => ai is not null;

    public ObservableCollection<TaskProposalChoice> Choices { get; } = [];

    /// <summary>The proposals are there to pick from (not asking, not added yet).</summary>
    public bool IsProposing => IsOpen && !IsBusy && !CanUndo && Choices.Count > 0;

    /// <summary>Asked, and nothing to pick from: a problem, or nothing found (Try again or Cancel).</summary>
    public bool IsStuck => IsOpen && !IsBusy && !CanUndo && Choices.Count == 0;

    public bool CanSpread => TaskDue is not null;

    public string SpreadText => TaskDue is { } due ? $"Spread due dates before {TaskProposals.DayText(due)}" : "";

    public int KeptCount => Choices.Count(c => c.Keep);

    /// <summary>"3 of 5 ticked".</summary>
    public string TickedText => $"{KeptCount} of {Choices.Count} ticked";

    /// <summary>The add button: "Add selected (3)" for steps, "Add 3 tasks" for a note.</summary>
    public string AddText => IsSteps ? $"Add selected ({KeptCount})" : KeptCount == 1 ? "Add 1 task" : $"Add {KeptCount} tasks";

    /// <summary>Proposes steps for a task (not a subtask: subtasks have none).</summary>
    public Task ForTaskAsync(string taskId)
    {
        if (store.GetItem(taskId) is not { IsSubtask: false } task) return Task.CompletedTask;
        Open(task.Title, taskId, "", () => ForTaskAsync(taskId));
        TaskDue = task.DueDate ?? TrackerDue.Day(task.DueAt, TimeZoneInfo.Local);
        SpreadDates = TaskDue is not null;
        var existing = store.Subtasks(taskId).Select(s => s.Value.Title).ToList();
        return ProposeAsync(TaskProposals.Steps(task.Title, task.Notes, existing, Today()), TaskProposals.MaxSteps);
    }

    /// <summary>Proposes the tasks in a note, for <paramref name="listName"/> (default: the first to-do list).</summary>
    public Task ForNoteAsync(string noteTitle, string noteText, string? listName = null)
    {
        Open(noteTitle, null, listName?.Trim() is { Length: > 0 } name ? name
            : store.Workspaces().FirstOrDefault(w => w.Value.Kind == WorkspaceKind.Tasks)?.Value.Name ?? TrackerTaskBridge.DefaultListName,
            () => ForNoteAsync(noteTitle, noteText, ListName));
        TaskDue = null;
        SpreadDates = false;
        return ProposeAsync(TaskProposals.FromNote(noteTitle, noteText, Today()), TaskProposals.MaxTasks);
    }

    private void Open(string title, string? taskId, string listName, Func<Task> again)
    {
        _asked++;
        _again = again;
        TaskId = taskId;
        Title = title;
        ListName = listName;
        Choices.Clear();
        CanUndo = false;
        _added = [];
        _madeList = null;
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
        Status = ai.Where(request.Feature) ?? "Asking AI…";
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
        if (!answer.Ok)
        {
            IsBusy = false;
            Status = answer.Problem!;
            return;
        }
        var proposals = TaskProposals.Parse(answer.Text, max);
        foreach (var p in proposals)
        {
            var choice = new TaskProposalChoice(p);
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TaskProposalChoice.Keep)) PlaceDates();
                if (e.PropertyName is nameof(TaskProposalChoice.Keep) or nameof(TaskProposalChoice.Title)) AddCommand.NotifyCanExecuteChanged();
            };
            Choices.Add(choice);
        }
        PlaceDates();
        IsBusy = false;
        Status = proposals.Count == 0 ? "AI found nothing to add." : answer.RanOn is { } pc ? $"Untick what you do not want · {pc}" : "Untick what you do not want";
        AddCommand.NotifyCanExecuteChanged();
    }

    partial void OnSpreadDatesChanged(bool value) => PlaceDates();

    /// <summary>Each ticked step's day: spread before the task's (in order) when asked, else the one AI gave.</summary>
    private void PlaceDates()
    {
        var kept = Choices.Where(c => c.Keep).ToList();
        var days = SpreadDates && TaskDue is { } due ? TaskProposals.SpreadBefore(Today(), due, kept.Count) : null;
        foreach (var choice in Choices)
        {
            var at = kept.IndexOf(choice);
            choice.Due = days is not null && at >= 0 ? days[at].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : choice.Task.Due;
            choice.DueText = choice.Keep && TaskProposals.DueDay(choice.Due) is { } day ? TaskProposals.DayText(day) : "";
        }
        OnPropertyChanged(nameof(KeptCount));
        OnPropertyChanged(nameof(TickedText));
        OnPropertyChanged(nameof(AddText));
        OnPropertyChanged(nameof(IsProposing));
        OnPropertyChanged(nameof(IsStuck));
    }

    private bool CanAdd() => !IsBusy && !CanUndo && Choices.Any(c => c.Keep && c.Title.Trim().Length > 0);

    /// <summary>Adds the ticked ones, as edited: as the task's checklist (its subtasks), or as tasks of the named list.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var kept = Choices.Where(c => c.Keep && c.Title.Trim().Length > 0)
            .Select(c => c.Task with { Title = c.Title.Trim(), Due = c.Due }).ToList();
        if (kept.Count == 0) return;
        try
        {
            string listId;
            if (TaskId is { } parent)
            {
                listId = store.GetItem(parent)?.WorkspaceId ?? throw new InvalidOperationException("The task is gone.");
            }
            else
            {
                listId = TaskProposals.ListNamed(store, ListName, out var made);
                if (made) _madeList = listId;
            }
            _added = TaskProposals.AddAll(store, listId, TaskId, kept);
            CanUndo = true;
            Status = TaskId is not null
                ? _added.Count == 1 ? "Added 1 step to this task's checklist." : $"Added {_added.Count} steps to this task's checklist."
                : $"Added {_added.Count} tasks to {store.GetWorkspace(listId)?.Name}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Status = ex.Message;
        }
        AddCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Takes the added batch back (and the list, when this made it); the proposals close.</summary>
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
        Close();
    }

    /// <summary>Asks again for the same task or note, for other proposals.</summary>
    [RelayCommand]
    private Task RetryAsync() => _again?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private void Close()
    {
        _asked++;
        IsBusy = false;
        IsOpen = false;
    }

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), TimeZoneInfo.Local).DateTime);
}
