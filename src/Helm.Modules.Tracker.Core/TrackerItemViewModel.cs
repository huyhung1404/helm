using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Tracker;

/// <summary>A workspace in the picker. Instances are kept across refreshes so the selection survives a rename.</summary>
public sealed partial class WorkspaceOption(string id, string name, WorkspaceKind kind) : ObservableObject
{
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private WorkspaceKind _kind = kind;

    public string Id { get; } = id;

    partial void OnKindChanged(WorkspaceKind value) => OnPropertyChanged(nameof(KindText));

    public string KindText => TrackerFormat.Kind(Kind);

    public override string ToString() => Name;
}

/// <summary>
/// One row of the open or completed list, with its inline editor. Rows are reused across refreshes (matched by id),
/// so an edit in progress is not lost when another device syncs a change.
/// </summary>
public sealed partial class TrackerItemViewModel : ObservableObject
{
    private readonly TrackerViewModel _owner;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private string _editNotes = "";
    [ObservableProperty] private int _editPriorityIndex;
    [ObservableProperty] private DateTime? _editDueDate;
    [ObservableProperty] private string _editDueTimeText = "";
    [ObservableProperty] private TimeSpan? _editDueTime;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddSubtaskCommand))] private string _newSubtaskTitle = "";
    [ObservableProperty] private int _editRepeatIndex;
    [ObservableProperty] private string _editRepeatDays = "";
    private bool _syncingTime;
    [ObservableProperty] private string _editPerson = "";
    [ObservableProperty] private string _editAmount = "";
    [ObservableProperty] private int _editDirectionIndex;
    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;

    /// <summary>Phone layout: the row's actions show after a tap on the row.</summary>
    [ObservableProperty] private bool _isExpanded;

    internal TrackerItemViewModel(TrackerViewModel owner, string id, TrackerItem item, TrackerWorkspace workspace, DateOnly today)
    {
        _owner = owner;
        Id = id;
        Item = item;
        Workspace = workspace;
        Today = today;
    }

    public string Id { get; }

    public TrackerItem Item { get; private set; }

    /// <summary>The task's subtasks (open first). Empty for a subtask itself.</summary>
    public System.Collections.ObjectModel.ObservableCollection<TrackerItemViewModel> Subtasks { get; } = [];

    public bool IsSubtask => Item.IsSubtask;

    public bool IsRepeating => Item.IsRepeating;

    /// <summary>"Every day", "Every day until 5/10" or "" (not repeating, or stopped).</summary>
    public string RepeatText => !Item.RepeatDaily ? "" : Item.RepeatUntil is { } until
        ? $"Every day until {until.ToString("d", System.Globalization.CultureInfo.CurrentCulture)}"
        : "Every day";

    /// <summary>How many days of this repeating task were done (all its days).</summary>
    public int Completions { get; internal set; }

    public bool CanHaveSubtasks => !IsDebt && !Item.IsSubtask;

    public bool HasSubtasks => Subtasks.Count > 0;

    /// <summary>"2/3" subtasks done.</summary>
    public string SubtaskProgress => HasSubtasks ? $"{Subtasks.Count(s => s.IsCompleted)}/{Subtasks.Count}" : "";

    internal void SetSubtasks(IReadOnlyList<TrackerItemViewModel> rows)
    {
        if (Subtasks.SequenceEqual(rows)) { OnPropertyChanged(nameof(SubtaskProgress)); return; }
        Subtasks.Clear();
        foreach (var row in rows) Subtasks.Add(row);
        OnPropertyChanged(nameof(HasSubtasks));
        OnPropertyChanged(nameof(SubtaskProgress));
    }

    public TrackerWorkspace Workspace { get; private set; }

    private DateOnly Today { get; set; }

    public bool IsDebt => Workspace.Kind == WorkspaceKind.Debts;

    public bool IsCompleted => Item.IsCompleted;

    public bool IsOpen => !Item.IsCompleted;

    /// <summary>
    /// The row's check box. Bound two-way (not through a command) so a click, the keyboard and screen readers
    /// (UI Automation toggles IsChecked without a click) all complete or reopen the item.
    /// </summary>
    public bool Done
    {
        get => Item.IsCompleted;
        set
        {
            if (value != Item.IsCompleted) _owner.ToggleComplete(this);
            // If the store refused (or the refresh is pending), put the box back to the real state.
            OnPropertyChanged();
        }
    }

    public bool IsStarted => Item.StartedExplicitly && !Item.IsCompleted;

    public bool CanStart => !Item.IsCompleted && Item.StartedAt is null && !IsDebt;

    public bool HasNotes => Item.Notes.Length > 0;

    public bool IsUrgent => Item.Priority == TrackerPriority.Urgent && IsOpen;

    public bool IsHigh => Item.Priority == TrackerPriority.High && IsOpen;

    public bool IsOverdue => IsOpen && Item.DueMoment(TimeZoneInfo.Local) is { } due && due < DateTimeOffset.Now;

    /// <summary>Main line: the title, or for a debt "Person — reason".</summary>
    public string Title
    {
        get
        {
            if (!IsDebt) return Item.Title;
            if (Item.Person.Length == 0) return Item.Title;
            return Item.Title.Length == 0 ? Item.Person : $"{Item.Person} — {Item.Title}";
        }
    }

    /// <summary>Right-hand amount for debts ("+1,500,000 ₫" owed to me, "−200,000 ₫" I owe).</summary>
    public string AmountText => IsDebt
        ? (Item.Direction == DebtDirection.TheyOweMe ? "+" : "−") + TrackerFormat.Money(Item.Amount, Workspace.Currency)
        : "";

    public bool OwedToMe => IsDebt && Item.Direction == DebtDirection.TheyOweMe;

    public bool IOwe => IsDebt && Item.Direction == DebtDirection.IOwe;

    public string PriorityText => TrackerFormat.Priority(Item.Priority);

    /// <summary>Secondary line: priority, due date, progress and timestamps, joined with " · ".</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Item.IsRepeating)
            {
                if (RepeatText.Length > 0) parts.Add(RepeatText);
                parts.Add(Completions == 1 ? "done 1 time" : $"done {Completions} times");
            }
            if (IsDebt) parts.Add(TrackerFormat.Direction(Item.Direction));
            // Urgent and High already show as a badge next to the title.
            else if (Item.Priority == TrackerPriority.Low) parts.Add(PriorityText);
            if (Item.CompletedAt is { } done)
            {
                parts.Add((IsDebt ? "Settled " : "Done ") + TrackerFormat.When(done));
                if (!IsDebt)
                {
                    // Same meaning as the history and the report: "took" counts from when it was added.
                    parts.Add("took " + TrackerFormat.Duration(done - Item.CreatedAt));
                    if (Item.StartedExplicitly && Item.StartedAt is { } started) parts.Add("worked " + TrackerFormat.Duration(done - started));
                }
            }
            else
            {
                if (Item.DueAt is not null || Item.DueDate is not null) parts.Add(TrackerFormat.Due(Item, DateTimeOffset.Now));
                if (Item.StartedExplicitly && Item.StartedAt is { } started) parts.Add("Started " + TrackerFormat.When(started));
                else parts.Add("Added " + TrackerFormat.When(Item.CreatedAt));
            }
            return string.Join(" · ", parts);
        }
    }

    internal void NotifyDetails() => OnPropertyChanged(nameof(Details));

    internal void Update(TrackerItem item, TrackerWorkspace workspace, DateOnly today)
    {
        if (item == Item && workspace == Workspace && today == Today) return;
        Item = item;
        Workspace = workspace;
        Today = today;
        OnPropertyChanged(string.Empty); // every computed property depends on the record
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleComplete() => _owner.ToggleComplete(this);

    [RelayCommand]
    private void Start() => _owner.Start(this);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);

    [RelayCommand]
    private void MoveUp() => _owner.Move(this, -1);

    [RelayCommand]
    private void MoveDown() => _owner.Move(this, 1);

    [RelayCommand]
    private void BeginEdit()
    {
        EditTitle = Item.Title;
        EditNotes = Item.Notes;
        EditPriorityIndex = (int)Item.Priority;
        var dueLocal = Item.DueAt is { } at ? TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local) : (DateTimeOffset?)null;
        EditDueDate = dueLocal?.Date ?? Item.DueDate?.ToDateTime(TimeOnly.MinValue);
        _syncingTime = true;
        EditDueTime = dueLocal?.TimeOfDay;
        EditDueTimeText = TrackerFormat.Time(EditDueTime);
        _syncingTime = false;
        EditPerson = Item.Person;
        EditAmount = Item.Amount == 0 ? "" : Item.Amount.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        EditDirectionIndex = (int)Item.Direction;
        EditRepeatIndex = !Item.RepeatDaily ? 0 : Item.RepeatUntil is null ? 1 : 2;
        EditRepeatDays = Item.RepeatUntil is { } until && Item.OccurrenceDate is { } day
            ? (until.DayNumber - day.DayNumber + 1).ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
        IsEditing = true;
        IsExpanded = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    partial void OnEditDueTimeTextChanged(string value)
    {
        if (_syncingTime || !TrackerFormat.TryParseTime(value, out var time)) return;
        _syncingTime = true;
        EditDueTime = time;
        _syncingTime = false;
    }

    partial void OnEditDueTimeChanged(TimeSpan? value)
    {
        if (_syncingTime) return;
        _syncingTime = true;
        EditDueTimeText = TrackerFormat.Time(value);
        _syncingTime = false;
    }

    private bool CanAddSubtask() => NewSubtaskTitle.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddSubtask))]
    private void AddSubtask()
    {
        if (_owner.AddSubtask(this, NewSubtaskTitle)) NewSubtaskTitle = "";
    }

    [RelayCommand]
    private void SaveEdit()
    {
        if (_owner.SaveEdit(this)) IsEditing = false;
    }
}
