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

    public bool IsOverdue => IsOpen && Item.DueDate is { } due && due < Today;

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
                if (Item.DueDate is { } due) parts.Add(TrackerFormat.Due(due, Today));
                if (Item.StartedExplicitly && Item.StartedAt is { } started) parts.Add("Started " + TrackerFormat.When(started));
                else parts.Add("Added " + TrackerFormat.When(Item.CreatedAt));
            }
            return string.Join(" · ", parts);
        }
    }

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
        EditDueDate = Item.DueDate?.ToDateTime(TimeOnly.MinValue);
        EditPerson = Item.Person;
        EditAmount = Item.Amount == 0 ? "" : Item.Amount.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        EditDirectionIndex = (int)Item.Direction;
        IsEditing = true;
        IsExpanded = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void SaveEdit()
    {
        if (_owner.SaveEdit(this)) IsEditing = false;
    }
}
