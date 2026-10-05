using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.Modules.Missions;

/// <summary>A mission in the picker.</summary>
public sealed partial class MissionChoice(string id) : ObservableObject
{
    [ObservableProperty] private string _name = "";

    public string Id { get; } = id;

    public override string ToString() => Name;
}

/// <summary>
/// A tick of the current step. Ticking it in the page (click, keyboard, or a screen reader's toggle) reports the change
/// through <c>changed</c>; updates from the store do not.
/// </summary>
public sealed partial class ChecklistRow(int index, Action<ChecklistRow, bool> changed) : ObservableObject
{
    private bool _fromStore;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isDone;

    public int Index { get; } = index;

    internal void Load(string text, bool done)
    {
        _fromStore = true;
        try
        {
            Text = text;
            IsDone = done;
        }
        finally
        {
            _fromStore = false;
        }
    }

    partial void OnIsDoneChanged(bool value)
    {
        if (!_fromStore) changed(this, value);
    }
}

/// <summary>
/// A resource of a step: its label, title and link, and when opened its text and table. The table is shown row by row
/// (the first cell large, the others after it), with the column names above, so any number of columns fits a phone.
/// </summary>
public sealed partial class ResourceRow : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;

    public ResourceRow(MissionResource resource)
    {
        Resource = resource;
        Url = MissionResources.IsWebAddress(resource.Url) ? resource.Url : null;
        Title = resource.Title.Length > 0 ? resource.Title
            : Url is not null ? Url
            : resource.Label.Length > 0 ? resource.Label
            : MissionResources.Line(resource);
        var host = Url is not null && Title != Url && Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : "";
        Summary = string.Join(" · ", new[] { MissionResources.Count(resource), host }.Where(s => s.Length > 0));
        Header = string.Join(" · ", resource.Columns.Where(c => c.Length > 0));
        Rows = resource.Rows.Select(r => new ResourceTableRow(r)).ToList();
    }

    public MissionResource Resource { get; }

    public string Label => Resource.Label;

    public bool HasLabel => Label.Length > 0;

    public string Title { get; }

    /// <summary>The http(s) address to open, or null.</summary>
    public string? Url { get; }

    public bool IsLink => Url is not null;

    /// <summary>"45 rows · hanzii.net".</summary>
    public string Summary { get; }

    public bool HasSummary => Summary.Length > 0;

    public string Text => Resource.Text;

    public bool HasText => Text.Length > 0;

    /// <summary>The column names, "Word · Pinyin · Meaning".</summary>
    public string Header { get; }

    public bool HasHeader => Header.Length > 0;

    public IReadOnlyList<ResourceTableRow> Rows { get; }

    public bool HasRows => Rows.Count > 0;

    /// <summary>A text or a table to open.</summary>
    public bool HasDetails => HasText || HasRows;

    public bool ShowDetails => IsExpanded && HasDetails;

    /// <summary>Details there to open, not open now.</summary>
    public bool ShowClosed => !IsExpanded && HasDetails;

    /// <summary>A link with nothing to open in the page: its title opens the link.</summary>
    public bool IsPlainLink => IsLink && !HasDetails;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(ShowClosed));
    }
}

/// <summary>A row of a resource's table: its first cell, then the other cells on one line.</summary>
public sealed class ResourceTableRow(IReadOnlyList<string> cells)
{
    public string First { get; } = cells.FirstOrDefault(c => c.Length > 0) ?? "";

    public string Rest { get; } = string.Join(" · ", cells.SkipWhile(c => c.Length == 0).Skip(1).Where(c => c.Length > 0));

    public bool HasRest => Rest.Length > 0;
}

public enum StepState
{
    Done,
    Skipped,
    Current,
    Upcoming,
}

/// <summary>A step in the roadmap: its state, details when expanded, and its editors.</summary>
public sealed partial class StepRow(string id) : ObservableObject
{
    [ObservableProperty] private int _number;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private StepState _state;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _doneWhen = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _canEdit;

    // Editing a step that is not done yet
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editDoneWhen = "";
    [ObservableProperty] private string _editDays = "";

    // The note of a done step
    [ObservableProperty] private bool _isEditingNote;
    [ObservableProperty] private string _noteDraft = "";

    private IReadOnlyList<MissionResource> _resourceSource = [];
    private string _resourceKey = "";
    private string? _builtKey;

    public string Id { get; } = id;

    /// <summary>The step's resources, made when the step is first opened (a roadmap can hold hundreds of steps).</summary>
    public ObservableCollection<ResourceRow> Resources { get; } = [];

    public bool HasResources => _resourceSource.Count > 0;

    /// <summary>Takes the step's resources; <paramref name="key"/> tells when they changed.</summary>
    internal void SetResources(IReadOnlyList<MissionResource> resources, string key)
    {
        if (key == _resourceKey) return;
        _resourceSource = resources;
        _resourceKey = key;
        OnPropertyChanged(nameof(HasResources));
        if (IsExpanded) BuildResources();
    }

    private void BuildResources()
    {
        if (_builtKey == _resourceKey) return;
        _builtKey = _resourceKey;
        Resources.Clear();
        foreach (var r in _resourceSource) Resources.Add(new ResourceRow(r));
    }

    public bool IsDone => State == StepState.Done;
    public bool IsSkipped => State == StepState.Skipped;
    public bool IsCurrent => State == StepState.Current;
    public bool IsUpcoming => State == StepState.Upcoming;
    public bool IsFinished => State is StepState.Done or StepState.Skipped;
    public bool HasDescription => Description.Length > 0;
    public bool HasDoneWhen => DoneWhen.Length > 0;
    public bool HasNote => Note.Length > 0;
    public bool ShowNote => HasNote && !IsEditingNote;
    public bool ShowBody => IsExpanded && !IsEditing;
    public bool CanEditNote => IsFinished && !IsEditingNote;
    public bool CanDelete => CanEdit && IsUpcoming;

    /// <summary>"✓", "⤼", "●", or the step's number.</summary>
    public string Marker => State switch
    {
        StepState.Done => "✓",
        StepState.Skipped => "⤼",
        StepState.Current => "●",
        _ => Number.ToString(System.Globalization.CultureInfo.CurrentCulture),
    };

    partial void OnStateChanged(StepState value)
    {
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsSkipped));
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(IsUpcoming));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(Marker));
        OnPropertyChanged(nameof(CanEditNote));
        OnPropertyChanged(nameof(CanDelete));
    }

    partial void OnNumberChanged(int value) => OnPropertyChanged(nameof(Marker));
    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(HasDescription));
    partial void OnDoneWhenChanged(string value) => OnPropertyChanged(nameof(HasDoneWhen));
    partial void OnCanEditChanged(bool value) => OnPropertyChanged(nameof(CanDelete));

    partial void OnNoteChanged(string value)
    {
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(ShowNote));
    }

    partial void OnIsEditingNoteChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNote));
        OnPropertyChanged(nameof(CanEditNote));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBody));
        if (value) BuildResources();
    }

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(ShowBody));
}

/// <summary>A phase in the roadmap, with its steps.</summary>
public sealed partial class PhaseRow(string key) : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _reward = "";
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _canClaimReward;
    [ObservableProperty] private string _rewardText = "";

    public string Key { get; } = key;

    public ObservableCollection<StepRow> Steps { get; } = [];

    public bool HasReward => RewardText.Length > 0;

    partial void OnRewardTextChanged(string value) => OnPropertyChanged(nameof(HasReward));
}

/// <summary>A mission in progress on the Today card: the step it is on, to finish right there.</summary>
public sealed partial class TodayRow(string missionId) : ObservableObject
{
    [ObservableProperty] private string _mission = "";
    [ObservableProperty] private string _step = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private bool _isBehind;

    public string MissionId { get; } = missionId;
}

/// <summary>A badge on the page: earned (with when) or what it takes.</summary>
public sealed partial class BadgeRow(string id) : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _earned;

    public string Id { get; } = id;

    public bool Locked => !Earned;

    partial void OnEarnedChanged(bool value) => OnPropertyChanged(nameof(Locked));
}

/// <summary>A step in the import preview; unticking it leaves it out.</summary>
public sealed partial class PreviewStep(StepDraft draft) : ObservableObject
{
    [ObservableProperty] private bool _include = true;

    public StepDraft Draft { get; } = draft;

    public string Title => Draft.Title;

    public string Details
    {
        get
        {
            var parts = new List<string> { MissionsFormat.Days(Draft.EstimateDays) };
            if (Draft.DoneWhen.Length > 0) parts.Add("done when: " + Draft.DoneWhen);
            if (Draft.Checklist is { Count: > 0 } c) parts.Add(c.Count == 1 ? "1 tick" : $"{c.Count} ticks");
            if (Draft.Resources is { Count: > 0 } r) parts.Add(r.Count == 1 ? "1 resource" : $"{r.Count} resources");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>A phase in the import preview.</summary>
public sealed class PreviewPhase(PhaseDraft draft, int number)
{
    public PhaseDraft Draft { get; } = draft;

    public string Title { get; } = $"{number}. {(draft.Title.Length > 0 ? draft.Title : $"Phase {number}")}";

    public string Reward { get; } = draft.Reward.Length > 0 ? "Reward: " + draft.Reward : "";

    public bool HasReward => Reward.Length > 0;

    public IReadOnlyList<PreviewStep> Steps { get; } = draft.Steps.Select(s => new PreviewStep(s)).ToList();
}

public enum CelebrationKind
{
    Phase,
    Mission,
}

/// <summary>What the celebration card shows after a phase or the whole mission.</summary>
public sealed record Celebration(
    CelebrationKind Kind,
    string MissionId,
    string? PhaseKey,
    string Title,
    string Message,
    string Reward,
    IReadOnlyList<string> Stats)
{
    public bool IsMission => Kind == CelebrationKind.Mission;
    public bool HasReward => Reward.Length > 0;
    public string RewardText => HasReward ? "Your reward: " + Reward : "";
}
