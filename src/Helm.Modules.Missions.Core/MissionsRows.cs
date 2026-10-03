using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
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

/// <summary>A link or a book title of the current step.</summary>
public sealed partial class ResourceRow(string text) : ObservableObject
{
    private static readonly Regex Link = new(@"https?://[^\s<>""')\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Text { get; } = text;

    /// <summary>The first web address in the text, or null for a plain book title.</summary>
    public string? Url { get; } = Link.Match(text) is { Success: true } m ? m.Value.TrimEnd('.', ',', ';') : null;

    public bool IsLink => Url is not null;
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

    public string Id { get; } = id;

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

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowBody));
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
