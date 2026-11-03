using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Tasks;

namespace Helm.Modules.Notes;

/// <summary>A task AI found in the note, ticked to be added.</summary>
public sealed partial class NoteTaskChoice(NoteTaskProposal task) : ObservableObject
{
    public NoteTaskProposal Task { get; } = task;

    [ObservableProperty] private bool _keep = true;

    public string Title => Task.Title;
    public string DueText => Task.DueText;
    public bool HasDue => Task.DueText.Length > 0;
}

/// <summary>
/// "Make tasks" on the open note: AI reads it and proposes its tasks; the user unticks what is not wanted, picks one
/// list for all of them and adds them to the to-do lists (<see cref="INoteTaskMaker"/>, Tracker). The lines that became
/// tasks get a "→ Tracker" tag in the note (<see cref="NoteTaskLines"/>); Undo takes the tasks and the tags back. The
/// panel names the lines it left out. UI thread only.
/// </summary>
public sealed partial class NoteTasksViewModel : ObservableObject
{
    private readonly INoteTaskMaker _maker;
    private readonly Func<(string Title, string Body)> _note;
    private readonly Action<string> _setBody;
    private NoteTaskBatch? _batch;
    private IReadOnlyList<string> _tagged = [];
    private int _asked;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsProposing))] [NotifyPropertyChangedFor(nameof(IsStuck))] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProposing))] [NotifyPropertyChangedFor(nameof(IsStuck))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProposing))] [NotifyPropertyChangedFor(nameof(IsStuck))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _isDone;

    /// <summary>While AI works, where it runs ("Haiku 5.5 · agent on HUNG-PC · 0đ"); then who answered, or a problem.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>The one list every task goes to.</summary>
    [ObservableProperty] private string _selectedList = "";

    /// <summary>"Skipped “Tuần sau về quê”: not a task."; "" when nothing was left out.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSkipped))] private string _skippedText = "";

    /// <summary>"4 tasks added to Nhà".</summary>
    [ObservableProperty] private string _doneText = "";

    public NoteTasksViewModel(INoteTaskMaker maker, Func<(string Title, string Body)> note, Action<string> setBody)
    {
        _maker = maker;
        _note = note;
        _setBody = setBody;
    }

    /// <summary>The tool the tasks go to ("Tracker").</summary>
    public string ToolName => _maker.Name;

    /// <summary>The lists to pick from, the default first.</summary>
    public ObservableCollection<string> Lists { get; } = [];

    public ObservableCollection<NoteTaskChoice> Choices { get; } = [];

    /// <summary>The proposals are there to pick from (not asking, not added).</summary>
    public bool IsProposing => IsOpen && !IsBusy && !IsDone && Choices.Count > 0;

    /// <summary>Asked, and nothing to pick from: a problem, or nothing found (Try again or Cancel).</summary>
    public bool IsStuck => IsOpen && !IsBusy && !IsDone && Choices.Count == 0;

    public bool HasSkipped => SkippedText.Length > 0;

    public int KeptCount => Choices.Count(c => c.Keep);

    /// <summary>"Add 3 tasks".</summary>
    public string AddText => KeptCount == 1 ? "Add 1 task" : $"Add {KeptCount} tasks";

    internal bool IsAvailable => _maker.IsAvailable;

    internal string ModuleId => _maker.ModuleId;

    /// <summary>Opens the panel and asks AI for the note's tasks.</summary>
    public async Task StartAsync()
    {
        var asked = ++_asked;
        var (title, body) = _note();
        // The list picked last time stays picked (clearing the lists may reset the picker's selection).
        var picked = SelectedList;
        Lists.Clear();
        foreach (var list in _maker.Lists()) Lists.Add(list);
        SelectedList = picked is not null && Lists.Contains(picked) ? picked : Lists.FirstOrDefault() ?? "";
        Choices.Clear();
        SkippedText = "";
        IsDone = false;
        _batch = null;
        IsOpen = true;
        IsBusy = true;
        Status = _maker.Where() ?? "Asking AI…";

        var answer = await _maker.ProposeAsync(title, body, CancellationToken.None).ConfigureAwait(true);
        // Closed, or asked again for another note meanwhile: this answer is stale.
        if (asked != _asked) return;
        IsBusy = false;
        if (answer.Problem is { } problem)
        {
            Status = problem;
            return;
        }
        foreach (var task in answer.Tasks)
        {
            var choice = new NoteTaskChoice(task);
            choice.PropertyChanged += (_, _) => Counted();
            Choices.Add(choice);
        }
        var tag = NoteTaskLines.Tag(_maker.Name);
        SkippedText = NoteTaskLines.Skipped(body, NoteTaskLines.Match(body, Sources(answer.Tasks), tag), tag);
        Status = answer.Tasks.Count == 0 ? "AI found no task in this note." : answer.RanOn is { } pc ? $"Untick what you do not want · {pc}" : "";
        Counted();
    }

    /// <summary>Asks again (after a problem, or for other proposals).</summary>
    [RelayCommand]
    private Task RetryAsync() => StartAsync();

    private void Counted()
    {
        OnPropertyChanged(nameof(KeptCount));
        OnPropertyChanged(nameof(AddText));
        OnPropertyChanged(nameof(IsProposing));
        OnPropertyChanged(nameof(IsStuck));
        AddCommand.NotifyCanExecuteChanged();
    }

    private bool CanAdd() => !IsBusy && !IsDone && Choices.Any(c => c.Keep);

    /// <summary>Adds the ticked tasks to the list picked and tags the lines they came from.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var kept = Choices.Where(c => c.Keep).Select(c => c.Task).ToList();
        if (kept.Count == 0) return;
        try
        {
            _batch = _maker.Add(SelectedList ?? "", kept);
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            return;
        }
        // The note as it is now (typing may have gone on): the lines found again there get the tag.
        var tag = NoteTaskLines.Tag(_maker.Name);
        var body = _note().Body;
        var lines = NoteTaskLines.Match(body, Sources(kept), tag).Where(i => i >= 0).ToList();
        if (lines.Count > 0)
        {
            var tagged = NoteTaskLines.AddTags(body, lines, tag);
            var split = NoteTaskLines.Split(tagged);
            _tagged = [.. lines.Select(i => split[i])];
            _setBody(tagged);
        }
        else _tagged = [];
        DoneText = kept.Count == 1 ? $"1 task added to {_batch.ListName}" : $"{kept.Count} tasks added to {_batch.ListName}";
        IsDone = true;
    }

    /// <summary>Takes the tasks back (and the list, when it was made for them) and the tags off the note's lines.</summary>
    [RelayCommand(CanExecute = nameof(IsDone))]
    private void Undo()
    {
        if (_batch is { } batch) _maker.Undo(batch);
        _batch = null;
        if (_tagged.Count > 0) _setBody(NoteTaskLines.RemoveTags(_note().Body, _tagged, NoteTaskLines.Tag(_maker.Name)));
        _tagged = [];
        IsDone = false;
        Counted();
    }

    /// <summary>Cancel, Done or the panel's close button: nothing more is added; what was added stays.</summary>
    [RelayCommand]
    public void Close()
    {
        _asked++;
        IsBusy = false;
        IsOpen = false;
        IsDone = false;
        _batch = null;
        _tagged = [];
    }

    private static IReadOnlyList<(string Title, string? Line)> Sources(IEnumerable<NoteTaskProposal> tasks) =>
        [.. tasks.Select(t => (t.Title, t.Line))];
}
