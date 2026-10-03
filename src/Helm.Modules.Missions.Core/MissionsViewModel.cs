using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Capture;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Missions;

/// <summary>
/// The Missions pages, shared by the Windows and Android apps: the mission picker, a new mission (the prompt to copy and
/// the JSON import with its preview), the current step and its actions, the roadmap, the celebrations, and the settings
/// both apps have. All members are used on the UI thread; store changes are posted through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class MissionsViewModel : ObservableObject
{
    private const string NoteTargetId = "note";

    private readonly MissionsStore _store;
    private readonly ISettingsStoreFactory _settingsFactory;
    private readonly ISettingsStore<MissionsSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly IProcessLauncher _launcher;
    private readonly MissionReminderService _reminders;
    private readonly Func<IEnumerable<ICaptureTarget>> _captureTargets;
    private readonly ILogger<MissionsViewModel> _logger;
    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, MissionChoice> _choices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhaseRow> _phaseRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StepRow> _stepRows = new(StringComparer.Ordinal);
    private int _refreshQueued;
    private bool _loading;
    private MissionImportResult? _import;

    [ObservableProperty] private MissionChoice? _selectedMission;

    /// <summary>A problem or a notice about the last action; null when there is nothing to say.</summary>
    [ObservableProperty] private string? _message;

    // New mission: the prompt
    [ObservableProperty] private bool _isPromptOpen;
    [ObservableProperty] private string _promptGoal = "";
    [ObservableProperty] private string _promptLevel = "";
    [ObservableProperty] private string _promptTime = "";
    [ObservableProperty] private DateTime? _promptDeadline;
    [ObservableProperty] private string _promptNotes = "";
    [ObservableProperty] private bool _promptCopied;

    // New mission: the import and its preview
    [ObservableProperty] private bool _isImportOpen;
    [ObservableProperty] private string _importText = "";
    [ObservableProperty] private string _importErrors = "";
    [ObservableProperty] private string _importWarnings = "";
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _previewGoal = "";
    [ObservableProperty] private string _previewReward = "";
    [ObservableProperty] private DateTime? _previewDeadline;
    [ObservableProperty] private string _previewNote = "";
    [ObservableProperty] private string _previewSummary = "";

    // The selected mission
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _goal = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private MissionStatus _status;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _paceText = "";
    [ObservableProperty] private bool _isBehind;
    [ObservableProperty] private string _projectionText = "";
    [ObservableProperty] private string _deadlineText = "";
    [ObservableProperty] private string _streakText = "";
    [ObservableProperty] private string _rewardText = "";
    [ObservableProperty] private bool _canClaimReward;
    [ObservableProperty] private string _plannedText = "";
    [ObservableProperty] private string _finishedText = "";
    [ObservableProperty] private bool _canUndo;

    // The current step
    [ObservableProperty] private bool _hasCurrentStep;
    [ObservableProperty] private string _currentPhaseText = "";
    [ObservableProperty] private string _currentNumberText = "";
    [ObservableProperty] private string _currentTitle = "";
    [ObservableProperty] private string _currentDescription = "";
    [ObservableProperty] private string _currentDoneWhen = "";
    [ObservableProperty] private string _currentStartedText = "";
    [ObservableProperty] private bool _canStartStep;
    [ObservableProperty] private string _completeNote = "";

    // The roadmap
    [ObservableProperty] private string _newStepTitle = "";
    [ObservableProperty] private bool _canAddStep;

    // The celebration card
    [ObservableProperty] private Celebration? _celebration;

    // Settings
    [ObservableProperty] private bool _playCelebrations;
    [ObservableProperty] private bool _confirmUntickedChecklist;
    [ObservableProperty] private bool _showFinishedMissions;
    [ObservableProperty] private bool _remindersEnabled;
    [ObservableProperty] private int _reminderHourIndex;

    // Editing the selected mission (settings page)
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private string _editGoal = "";
    [ObservableProperty] private string _editReward = "";
    [ObservableProperty] private DateTime? _editDeadline;

    // Planning the rest of the way again (the import panel then takes the new plan)
    [ObservableProperty] private bool _isReplanOpen;
    [ObservableProperty] private bool _isReplan;
    [ObservableProperty] private string _replanReason = "";

    // Notes is there and on: the summary can be saved as a note
    [ObservableProperty] private bool _canSaveToNotes;

    public MissionsViewModel(
        MissionsStore store,
        ISettingsStoreFactory settings,
        IUiDispatcher ui,
        IDialogService dialogs,
        IClipboardService clipboard,
        IProcessLauncher launcher,
        MissionReminderService reminders,
        IServiceProvider services,
        ILogger<MissionsViewModel> logger)
        // Capture targets are resolved when used: Notes may register after Missions, and its target is optional.
        : this(store, settings, ui, dialogs, clipboard, launcher, reminders, () => (IEnumerable<ICaptureTarget>?)services.GetService(typeof(IEnumerable<ICaptureTarget>)) ?? [],
            logger, TimeZoneInfo.Local)
    {
    }

    internal MissionsViewModel(
        MissionsStore store,
        ISettingsStoreFactory settings,
        IUiDispatcher ui,
        IDialogService dialogs,
        IClipboardService clipboard,
        IProcessLauncher launcher,
        MissionReminderService reminders,
        Func<IEnumerable<ICaptureTarget>> captureTargets,
        ILogger<MissionsViewModel> logger,
        TimeZoneInfo zone)
    {
        _store = store;
        _settingsFactory = settings;
        _settings = settings.Get<MissionsSettings>(MissionsIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _launcher = launcher;
        _reminders = reminders;
        _captureTargets = captureTargets;
        _logger = logger;
        _zone = zone;

        _loading = true;
        var s = _settings.Current;
        PlayCelebrations = s.PlayCelebrations;
        ConfirmUntickedChecklist = s.ConfirmUntickedChecklist;
        ShowFinishedMissions = s.ShowFinishedMissions;
        RemindersEnabled = s.RemindersEnabled;
        ReminderHourIndex = Math.Clamp(s.ReminderHour, 0, 23);
        _loading = false;

        _store.Changed += (_, _) => ScheduleRefresh();
        Refresh();
    }

    /// <summary>Raised when a step, a phase or the whole mission is done here, so the page can play its animation.</summary>
    public event EventHandler<CelebrationKind?>? Celebrated;

    public ObservableCollection<MissionChoice> Missions { get; } = [];

    public ObservableCollection<ChecklistRow> CurrentChecklist { get; } = [];

    public ObservableCollection<ResourceRow> CurrentResources { get; } = [];

    public ObservableCollection<PhaseRow> Phases { get; } = [];

    public ObservableCollection<PreviewPhase> PreviewPhases { get; } = [];

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasMissions => Missions.Count > 0;
    public bool HasNoMissions => Missions.Count == 0 && !IsNewOpen;
    public bool HasSelection => SelectedMission is not null;
    public bool IsNewOpen => IsPromptOpen || IsImportOpen || IsReplanOpen;
    public bool ShowMission => HasSelection && !IsNewOpen;
    public bool HasImportErrors => ImportErrors.Length > 0;
    public bool HasImportWarnings => ImportWarnings.Length > 0;
    public bool HasPreviewNote => PreviewNote.Length > 0;
    public bool CanCopyPrompt => PromptGoal.Trim().Length > 0;
    public bool CanCreate => HasPreview && (IsReplan || PreviewTitle.Trim().Length > 0) && PreviewPhases.Any(p => p.Steps.Any(s => s.Include));

    /// <summary>The import panel takes a new mission (title, goal and reward to edit), not a new plan for this one.</summary>
    public bool IsNewMission => !IsReplan;

    public string ImportHeading => IsReplan ? "2. Paste the AI's new plan" : "2. Paste the AI's answer";

    public string CreateLabel => IsReplan ? "Replace the remaining steps" : "Create mission";

    /// <summary>"00:00" … "23:00" in the current culture's short time format; the index is the hour.</summary>
    public IReadOnlyList<string> ReminderHourNames { get; } =
        Enumerable.Range(0, 24).Select(h => new DateTime(2000, 1, 1, h, 0, 0).ToString("t", CultureInfo.CurrentCulture)).ToList();

    public bool IsPlanned => Status == MissionStatus.Planned;
    public bool IsActive => Status == MissionStatus.Active;
    public bool IsPaused => Status == MissionStatus.Paused;
    public bool IsCompleted => Status == MissionStatus.Completed;
    public bool IsAbandoned => Status == MissionStatus.Abandoned;
    public bool IsOpenForChanges => Status is MissionStatus.Planned or MissionStatus.Active or MissionStatus.Paused;

    /// <summary>The current step is shown with its actions only while the mission runs.</summary>
    public bool ShowCurrentStep => HasCurrentStep && (IsActive || IsPaused);

    public bool HasGoal => Goal.Length > 0;
    public bool HasNote => Note.Length > 0;
    public bool HasPace => PaceText.Length > 0;
    public bool HasProjection => ProjectionText.Length > 0;
    public bool HasDeadline => DeadlineText.Length > 0;
    public bool HasStreak => StreakText.Length > 0;
    public bool HasReward => RewardText.Length > 0;
    public bool HasCurrentDescription => CurrentDescription.Length > 0;
    public bool HasCurrentDoneWhen => CurrentDoneWhen.Length > 0;
    public bool HasCurrentChecklist => CurrentChecklist.Count > 0;
    public bool HasCurrentResources => CurrentResources.Count > 0;
    public bool HasCelebration => Celebration is not null;
    public bool IsMissionCelebration => Celebration?.IsMission == true;

    /// <summary>Confetti with the celebration card (the card itself always shows).</summary>
    public bool PlayConfetti => Celebration is not null && PlayCelebrations;

    // ---- Picker --------------------------------------------------------------------------------------------------

    partial void OnSelectedMissionChanged(MissionChoice? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowMission));
        if (!_loading && value is not null) _settings.Update(s => s.SelectedMissionId = value.Id);
        CompleteNote = "";
        NewStepTitle = "";
        RefreshDetails();
    }

    // ---- New mission ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private void OpenPrompt()
    {
        SetReplan(false);
        IsImportOpen = false;
        IsReplanOpen = false;
        IsPromptOpen = true;
        PromptCopied = false;
    }

    [RelayCommand]
    private void OpenImport()
    {
        SetReplan(false);
        IsPromptOpen = false;
        IsReplanOpen = false;
        IsImportOpen = true;
    }

    /// <summary>"Back" in the import panel: to the prompt it came from.</summary>
    [RelayCommand]
    private void BackToPrompt()
    {
        if (IsReplan) OpenReplan();
        else OpenPrompt();
    }

    [RelayCommand]
    private void CloseNew()
    {
        IsPromptOpen = false;
        IsImportOpen = false;
        IsReplanOpen = false;
        SetReplan(false);
        ImportText = "";
    }

    /// <summary>Switches the import panel between a new mission and a new plan for the selected one.</summary>
    private void SetReplan(bool replan)
    {
        if (IsReplan == replan) return;
        IsReplan = replan;
        ImportText = "";
        ParseImport();
    }

    partial void OnIsPromptOpenChanged(bool value) => OnNewChanged();

    partial void OnIsImportOpenChanged(bool value) => OnNewChanged();

    partial void OnIsReplanOpenChanged(bool value) => OnNewChanged();

    partial void OnIsReplanChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNewMission));
        OnPropertyChanged(nameof(ImportHeading));
        OnPropertyChanged(nameof(CreateLabel));
        OnPropertyChanged(nameof(CanCreate));
    }

    private void OnNewChanged()
    {
        OnPropertyChanged(nameof(IsNewOpen));
        OnPropertyChanged(nameof(ShowMission));
        OnPropertyChanged(nameof(HasNoMissions));
    }

    // ---- Planning the rest again ---------------------------------------------------------------------------------

    /// <summary>Opens the re-plan panel for the selected mission (what changed, then the prompt to copy).</summary>
    [RelayCommand]
    private void OpenReplan()
    {
        if (SelectedMission is null || !IsOpenForChanges) return;
        IsPromptOpen = false;
        IsImportOpen = false;
        SetReplan(true);
        IsReplanOpen = true;
        PromptCopied = false;
    }

    [RelayCommand]
    private void CopyReplanPrompt()
    {
        if (SelectedMission is not { } m || _store.GetMission(m.Id) is not { } mission) return;
        var steps = _store.Steps(m.Id).Select(s => s.Value).ToList();
        var pace = MissionPace.Compute(mission, steps, _store.History(m.Id), _store.Now, _zone);
        _clipboard.SetText(MissionPrompt.BuildReplan(mission, steps, pace, Today, ReplanReason));
        PromptCopied = true;
        Message = "Prompt copied, with your progress in it. Paste it into any AI, then paste its new plan here.";
    }

    /// <summary>From the re-plan prompt to the box for the AI's new plan.</summary>
    [RelayCommand]
    private void OpenReplanImport()
    {
        IsReplanOpen = false;
        IsImportOpen = true;
    }

    partial void OnPromptGoalChanged(string value) => OnPropertyChanged(nameof(CanCopyPrompt));

    [RelayCommand]
    private void CopyPrompt()
    {
        if (!CanCopyPrompt) return;
        var deadline = PromptDeadline is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
        _clipboard.SetText(MissionPrompt.Build(new MissionPromptInput(PromptGoal, PromptLevel, PromptTime, deadline, PromptNotes)));
        PromptCopied = true;
        Message = "Prompt copied. Paste it into ChatGPT, Gemini or Claude, then import the JSON it gives back.";
    }

    /// <summary>The template with placeholders (settings page), for use outside the form.</summary>
    [RelayCommand]
    private void CopyTemplate()
    {
        _clipboard.SetText(MissionPrompt.Template);
        Message = "The prompt template is copied. Replace the parts in <angle brackets> before you send it.";
    }

    partial void OnImportTextChanged(string value) => ParseImport();

    private void ParseImport()
    {
        // A new plan for a mission keeps the mission's title, so it may come without one.
        _import = ImportText.Trim().Length == 0 ? null : MissionImport.Parse(ImportText, requireTitle: !IsReplan);
        ImportErrors = _import is { Errors.Count: > 0 } bad ? string.Join("\n", bad.Errors) : "";
        ImportWarnings = _import is { Warnings.Count: > 0 } warn ? string.Join("\n", warn.Warnings) : "";
        PreviewPhases.Clear();
        if (_import?.Draft is { } draft)
        {
            PreviewTitle = draft.Title;
            PreviewGoal = draft.Goal;
            PreviewReward = draft.Reward;
            var keep = IsReplan && SelectedMission is { } sel ? _store.GetMission(sel.Id)?.Deadline : null;
            PreviewDeadline = (draft.Deadline ?? keep)?.ToDateTime(TimeOnly.MinValue);
            PreviewNote = draft.Note;
            var number = 0;
            foreach (var phase in draft.Phases)
            {
                var row = new PreviewPhase(phase, ++number);
                foreach (var step in row.Steps) step.PropertyChanged += (_, _) => UpdatePreviewSummary();
                PreviewPhases.Add(row);
            }
            HasPreview = true;
        }
        else
        {
            HasPreview = false;
            PreviewNote = "";
        }
        UpdatePreviewSummary();
    }

    partial void OnImportErrorsChanged(string value) => OnPropertyChanged(nameof(HasImportErrors));
    partial void OnImportWarningsChanged(string value) => OnPropertyChanged(nameof(HasImportWarnings));
    partial void OnPreviewNoteChanged(string value) => OnPropertyChanged(nameof(HasPreviewNote));
    partial void OnPreviewTitleChanged(string value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnHasPreviewChanged(bool value) => OnPropertyChanged(nameof(CanCreate));
    partial void OnPreviewDeadlineChanged(DateTime? value) => UpdatePreviewSummary();

    private void UpdatePreviewSummary()
    {
        OnPropertyChanged(nameof(CanCreate));
        if (!HasPreview)
        {
            PreviewSummary = "";
            return;
        }
        var steps = PreviewPhases.SelectMany(p => p.Steps).Where(s => s.Include).ToList();
        var phases = PreviewPhases.Count(p => p.Steps.Any(s => s.Include));
        var days = steps.Sum(s => s.Draft.EstimateDays);
        var today = Today;
        var finish = today.AddDays((int)Math.Ceiling(days));
        var text = $"{Plural(phases, "phase")} · {Plural(steps.Count, "step")} · about {MissionsFormat.Days(days)}. Started today, it ends around {MissionsFormat.Date(finish, today)}";
        if (IsReplan && SelectedMission is { } m)
        {
            var left = _store.Steps(m.Id).Count(s => !s.Value.IsDone);
            text = $"Replaces the {Plural(left, "step")} not done yet (the one you are on too) with {Plural(steps.Count, "step")}, about {MissionsFormat.Days(days)}. " +
                   $"From today it ends around {MissionsFormat.Date(finish, today)}";
        }
        if (PreviewDeadline is { } d)
        {
            var late = finish.DayNumber - DateOnly.FromDateTime(d).DayNumber;
            text += late > 0 ? $", {Plural(late, "day")} after the deadline." : ", before the deadline.";
        }
        else text += ".";
        PreviewSummary = text;
    }

    [RelayCommand]
    private void CreateMission()
    {
        if (_import?.Draft is not { } draft || !CanCreate) return;
        try
        {
            var phases = PreviewPhases
                .Select(p => p.Draft with { Steps = p.Steps.Where(s => s.Include).Select(s => s.Draft).ToList() })
                .Where(p => p.Steps.Count > 0)
                .ToList();
            var edited = draft with
            {
                Title = PreviewTitle,
                Goal = PreviewGoal,
                Reward = PreviewReward,
                Deadline = PreviewDeadline is { } d ? DateOnly.FromDateTime(d) : null,
                Phases = phases,
            };
            if (IsReplan)
            {
                if (SelectedMission is not { } sel) return;
                if (!_store.Replan(sel.Id, edited))
                {
                    Message = $"The new plan cannot be used: a mission has at most {MissionLimits.Steps} steps in {MissionLimits.Phases} phases, and a finished one cannot be planned again.";
                    return;
                }
                CloseNew();
                Refresh();
                Message = $"The rest of the plan is replaced: {Plural(edited.StepCount, "step")} to go.";
                return;
            }
            var id = _store.Create(edited, MissionSource.Import);
            CloseNew();
            Refresh();
            Select(id);
            Message = $"“{edited.Title}” is ready. Press Start mission when you begin: the pace counts from then.";
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating a mission failed");
            Message = $"Could not create the mission: {ex.Message}";
        }
    }

    // ---- Running the mission -------------------------------------------------------------------------------------

    [RelayCommand]
    private void StartMission()
    {
        if (SelectedMission is { } m && _store.Start(m.Id)) Message = "Mission started. Here is your first step.";
    }

    [RelayCommand]
    private void StartStep()
    {
        if (SelectedMission is { } m) _store.StartStep(m.Id);
    }

    /// <summary>A checklist item ticked or unticked on the page: stored unless the store already says so.</summary>
    private void OnChecklistChanged(ChecklistRow row, bool done)
    {
        if (SelectedMission is not { } m || _store.CurrentStep(m.Id) is not { } current) return;
        if (row.Index < current.Value.Checklist.Count && current.Value.Checklist[row.Index].IsDone != done && !_store.ToggleChecklist(m.Id, row.Index))
            ScheduleRefresh(); // not running (paused): show the stored state again
    }

    [RelayCommand]
    private void OpenResource(ResourceRow? row)
    {
        if (row?.Url is not { } url) return;
        try
        {
            _launcher.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening a mission resource failed");
            Message = $"Could not open the link: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CompleteStepAsync()
    {
        if (SelectedMission is not { } m || _store.CurrentStep(m.Id) is not { } current) return;
        var open = current.Value.Checklist.Count(c => !c.IsDone);
        if (open > 0 && ConfirmUntickedChecklist
            && !await _dialogs.ConfirmAsync("Complete this step?", $"{Plural(open, "checklist item")} {(open == 1 ? "is" : "are")} not ticked. Complete the step anyway?", "Complete"))
            return;
        if (_store.Complete(m.Id, CompleteNote) is { } outcome) Finished(m.Id, outcome, skipped: false);
    }

    [RelayCommand]
    private async Task SkipStepAsync()
    {
        if (SelectedMission is not { } m || _store.CurrentStep(m.Id) is not { } current) return;
        if (!await _dialogs.ConfirmAsync("Skip this step?", $"“{current.Value.Title}” is marked as skipped and the next step starts. Undo brings it back.", "Skip"))
            return;
        if (_store.Skip(m.Id, CompleteNote) is { } outcome) Finished(m.Id, outcome, skipped: true);
    }

    private void Finished(string missionId, StepOutcome outcome, bool skipped)
    {
        CompleteNote = "";
        Refresh();
        Message = outcome.MissionCompleted
            ? null
            : $"Step {outcome.Done} of {outcome.Total} {(skipped ? "skipped" : "done")}." + (outcome.NextStepTitle is { } next ? $" Next: {next}" : "");
        if (outcome.MissionCompleted)
        {
            // The refresh after the store change may have shown it already.
            if (Celebration is not { IsMission: true } shown || shown.MissionId != missionId) ShowMissionCelebration(missionId);
            Celebrated?.Invoke(this, CelebrationKind.Mission);
        }
        else if (outcome.PhaseCompleted is { } phase)
        {
            ShowPhaseCelebration(missionId, phase.Key);
            Celebrated?.Invoke(this, CelebrationKind.Phase);
        }
        else if (!skipped)
        {
            Celebrated?.Invoke(this, null);
        }
    }

    [RelayCommand]
    private void UndoStep()
    {
        if (SelectedMission is not { } m) return;
        var last = _store.Steps(m.Id).LastOrDefault(s => s.Value.IsDone);
        if (last is not null && _store.ReopenLast(m.Id))
        {
            _settings.Update(s => s.CelebratedMissionIds.Remove(m.Id));
            Message = $"“{last.Value.Title}” is open again.";
        }
    }

    [RelayCommand]
    private void Pause()
    {
        if (SelectedMission is { } m && _store.Pause(m.Id)) Message = "Paused. The time it is paused does not count against your pace.";
    }

    [RelayCommand]
    private void Resume()
    {
        if (SelectedMission is { } m && _store.Resume(m.Id)) Message = null;
    }

    [RelayCommand]
    private async Task AbandonAsync()
    {
        if (SelectedMission is not { } m || _store.GetMission(m.Id) is not { } mission) return;
        if (!await _dialogs.ConfirmAsync("Abandon this mission?", $"“{mission.Title}” stops here. Its steps and history are kept, and you can take it up again later.", "Abandon"))
            return;
        if (_store.Abandon(m.Id)) Message = "Abandoned. Resume brings it back.";
    }

    [RelayCommand]
    private void ClaimReward()
    {
        if (SelectedMission is { } m && _store.ClaimReward(m.Id)) Message = "Enjoy it, you earned it.";
    }

    [RelayCommand]
    private void ClaimPhaseReward(PhaseRow? phase)
    {
        if (SelectedMission is { } m && phase is not null && _store.ClaimReward(m.Id, phase.Key)) Message = "Enjoy it, you earned it.";
    }

    /// <summary>Notes' capture target while Notes is on (Missions does not reference Notes; Quick Capture's target does it).</summary>
    private ICaptureTarget? NoteTarget()
    {
        try
        {
            var enabled = _settingsFactory.Get<GeneralSettings>(GeneralSettings.StoreId).Current.EnabledModules;
            return _captureTargets().FirstOrDefault(t => t.Id == NoteTargetId && (!enabled.TryGetValue(t.ModuleId, out var on) || on));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Looking for Notes failed");
            return null;
        }
    }

    /// <summary>The summary as a new note: "Mission: title" on the first line, the summary under it.</summary>
    [RelayCommand]
    private void SaveSummaryToNotes()
    {
        if (SelectedMission is not { } m || _store.GetMission(m.Id) is not { } mission) return;
        if (NoteTarget() is not { } notes)
        {
            Message = "Turn on Notes to save the summary there.";
            return;
        }
        var steps = _store.Steps(m.Id).Select(s => s.Value).ToList();
        var pace = MissionPace.Compute(mission, steps, _store.History(m.Id), _store.Now, _zone);
        var summary = MissionsFormat.Summary(mission, steps, pace, _store.Now, _zone);
        // The summary's own heading becomes the note's title.
        var body = string.Join('\n', summary.Split('\n').Skip(1)).Trim();
        var result = notes.Capture($"Mission: {mission.Title}\n{body}");
        Message = result.Message;
    }

    [RelayCommand]
    private void CopySummary()
    {
        if (SelectedMission is not { } m || _store.GetMission(m.Id) is not { } mission) return;
        var steps = _store.Steps(m.Id).Select(s => s.Value).ToList();
        var pace = MissionPace.Compute(mission, steps, _store.History(m.Id), _store.Now, _zone);
        _clipboard.SetText(MissionsFormat.Summary(mission, steps, pace, _store.Now, _zone));
        Message = "Summary copied (Markdown).";
    }

    [RelayCommand]
    private void CopyJson()
    {
        if (SelectedMission is not { } m || _store.GetMission(m.Id) is not { } mission) return;
        _clipboard.SetText(MissionsFormat.Json(mission, _store.Steps(m.Id).Select(s => s.Value).ToList()));
        Message = "The mission is copied as JSON: a backup, or something to show an AI.";
    }

    // ---- Celebrations --------------------------------------------------------------------------------------------

    private void ShowPhaseCelebration(string missionId, string phaseKey)
    {
        if (_store.GetMission(missionId) is not { } mission || mission.Phase(phaseKey) is not { } phase) return;
        var steps = _store.Steps(missionId).Select(s => s.Value).Where(s => s.PhaseKey == phaseKey).ToList();
        var index = mission.PhaseIndex(phaseKey);
        var stats = new List<string>();
        var start = steps.Select(s => s.StartedAt).Where(s => s is not null).DefaultIfEmpty().Min();
        var end = steps.Select(s => s.CompletedAt).Where(s => s is not null).DefaultIfEmpty().Max();
        if (start is { } a && end is { } b) stats.Add($"Took {MissionsFormat.Took(b - a)} (planned {MissionsFormat.Days(steps.Sum(s => s.EstimateDays))})");
        stats.Add($"{Plural(steps.Count(s => !s.Skipped), "step")} done" + (steps.Any(s => s.Skipped) ? $", {steps.Count(s => s.Skipped)} skipped" : ""));
        var next = index + 1 < mission.Phases.Count ? mission.Phases[index + 1].Title : null;
        Celebration = new Celebration(CelebrationKind.Phase, missionId, phaseKey,
            $"Phase {index + 1} of {mission.Phases.Count} complete",
            next is null ? phase.Title : $"{phase.Title} is behind you. Next up: {next}.",
            phase.RewardClaimedAt is null ? phase.Reward : "",
            stats);
    }

    private void ShowMissionCelebration(string missionId)
    {
        if (_store.GetMission(missionId) is not { } mission) return;
        var steps = _store.Steps(missionId).Select(s => s.Value).ToList();
        var pace = MissionPace.Compute(mission, steps, _store.History(missionId), _store.Now, _zone);
        var stats = new List<string>();
        if (mission.StartedAt is not null) stats.Add($"Finished in {MissionsFormat.Days(pace.ElapsedDays)} (planned {MissionsFormat.Days(pace.PlannedDays)})");
        if (mission.Deadline is { } d && mission.CompletedAt is { } c)
        {
            var late = MissionPace.Day(c, _zone).DayNumber - d.DayNumber;
            stats.Add(late > 0 ? $"{Plural(late, "day")} after the deadline" : late == 0 ? "Right on the deadline" : $"{Plural(-late, "day")} before the deadline");
        }
        stats.Add($"{Plural(pace.Done - pace.Skipped, "step")} done" + (pace.Skipped > 0 ? $", {pace.Skipped} skipped" : ""));
        if (pace.LongestStreak > 1) stats.Add($"Longest streak: {Plural(pace.LongestStreak, "day")}");
        Celebration = new Celebration(CelebrationKind.Mission, missionId, null, "Mission complete!", mission.Goal.Length > 0 ? mission.Goal : mission.Title,
            mission.RewardClaimedAt is null ? mission.Reward : "", stats);
        _settings.Update(s =>
        {
            if (!s.CelebratedMissionIds.Contains(missionId)) s.CelebratedMissionIds.Add(missionId);
        });
    }

    [RelayCommand]
    private void ClaimCelebrationReward()
    {
        if (Celebration is not { } c) return;
        _store.ClaimReward(c.MissionId, c.PhaseKey);
        Celebration = c with { Reward = "" };
        Message = "Enjoy it, you earned it.";
    }

    [RelayCommand]
    private void DismissCelebration() => Celebration = null;

    partial void OnCelebrationChanged(Celebration? value)
    {
        OnPropertyChanged(nameof(HasCelebration));
        OnPropertyChanged(nameof(IsMissionCelebration));
        OnPropertyChanged(nameof(PlayConfetti));
    }

    // ---- The roadmap ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleStep(StepRow? row)
    {
        if (row is not null && !row.IsEditing) row.IsExpanded = !row.IsExpanded;
    }

    [RelayCommand]
    private void EditStep(StepRow? row)
    {
        if (row is null || !row.CanEdit || _store.GetStep(row.Id) is not { } step) return;
        row.EditTitle = step.Title;
        row.EditDescription = step.Description;
        row.EditDoneWhen = step.DoneWhen;
        row.EditDays = step.EstimateDays.ToString("0.##", CultureInfo.CurrentCulture);
        row.IsExpanded = true;
        row.IsEditing = true;
    }

    [RelayCommand]
    private void SaveStep(StepRow? row)
    {
        if (row is null) return;
        if (row.EditTitle.Trim().Length == 0)
        {
            Message = "A step needs a title.";
            return;
        }
        if (!TryDays(row.EditDays, out var days))
        {
            Message = "Days is a number, e.g. 2 or 0.5.";
            return;
        }
        _store.UpdateStep(row.Id, row.EditTitle, row.EditDescription, row.EditDoneWhen, days);
        row.IsEditing = false;
        Message = null;
        RefreshDetails();
    }

    [RelayCommand]
    private void CancelStep(StepRow? row)
    {
        if (row is not null) row.IsEditing = false;
    }

    [RelayCommand]
    private async Task DeleteStepAsync(StepRow? row)
    {
        if (row is null || !row.CanDelete) return;
        if (!await _dialogs.ConfirmAsync("Delete this step?", $"“{row.Title}” is removed from the mission on all your devices.", "Delete")) return;
        if (!_store.DeleteStep(row.Id)) Message = "That step cannot be deleted: a mission keeps at least one step.";
    }

    [RelayCommand]
    private void EditNote(StepRow? row)
    {
        if (row is null) return;
        row.NoteDraft = row.Note;
        row.IsExpanded = true;
        row.IsEditingNote = true;
    }

    [RelayCommand]
    private void SaveNote(StepRow? row)
    {
        if (row is null) return;
        _store.SetNote(row.Id, row.NoteDraft);
        row.IsEditingNote = false;
    }

    [RelayCommand]
    private void CancelNote(StepRow? row)
    {
        if (row is not null) row.IsEditingNote = false;
    }

    [RelayCommand]
    private void AddStep()
    {
        if (SelectedMission is not { } m || NewStepTitle.Trim().Length == 0) return;
        if (_store.AddStep(m.Id, new StepDraft(NewStepTitle.Trim())) is null)
        {
            Message = "A step cannot be added here.";
            return;
        }
        NewStepTitle = "";
        Message = "Step added at the end. Open it in the roadmap to add details.";
    }

    // ---- Settings page -------------------------------------------------------------------------------------------

    partial void OnPlayCelebrationsChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayConfetti));
        if (!_loading) _settings.Update(s => s.PlayCelebrations = value);
    }

    partial void OnConfirmUntickedChecklistChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.ConfirmUntickedChecklist = value);
    }

    partial void OnRemindersEnabledChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.RemindersEnabled = value);
    }

    partial void OnReminderHourIndexChanged(int value)
    {
        if (!_loading && value >= 0) _settings.Update(s => s.ReminderHour = Math.Clamp(value, 0, 23));
    }

    /// <summary>Shows today's reminder right away (to try the notification), whatever the hour.</summary>
    [RelayCommand]
    private void RemindNow()
    {
        try
        {
            Message = _reminders.RemindNow(_zone) ? null : "No mission in progress is waiting for a step, so there is nothing to remind you about.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Missions reminder failed");
            Message = $"Could not show the reminder: {ex.Message}";
        }
    }

    partial void OnShowFinishedMissionsChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(s => s.ShowFinishedMissions = value);
        Refresh();
    }

    partial void OnEditTitleChanged(string value)
    {
        if (_loading || SelectedMission is not { } m) return;
        if (value.Trim().Length == 0)
        {
            Message = "A mission needs a title.";
            return;
        }
        _store.UpdateMission(m.Id, title: value);
    }

    partial void OnEditGoalChanged(string value)
    {
        if (!_loading && SelectedMission is { } m) _store.UpdateMission(m.Id, goal: value);
    }

    partial void OnEditRewardChanged(string value)
    {
        if (!_loading && SelectedMission is { } m) _store.UpdateMission(m.Id, reward: value);
    }

    partial void OnEditDeadlineChanged(DateTime? value)
    {
        if (_loading || SelectedMission is not { } m) return;
        if (value is { } d) _store.UpdateMission(m.Id, deadline: DateOnly.FromDateTime(d));
        else _store.UpdateMission(m.Id, clearDeadline: true);
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedMission is { } m) _store.Move(m.Id, -1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedMission is { } m) _store.Move(m.Id, +1);
    }

    [RelayCommand]
    private async Task DeleteMissionAsync()
    {
        if (SelectedMission is not { } m || _store.GetMission(m.Id) is not { } mission) return;
        if (!await _dialogs.ConfirmAsync("Delete this mission?", $"“{mission.Title}” and its steps are removed from all your devices. This cannot be undone.", "Delete"))
            return;
        _store.Delete(m.Id);
        _settings.Update(s => s.CelebratedMissionIds.Remove(m.Id));
        Message = $"“{mission.Title}” was deleted.";
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    // ---- Refresh -------------------------------------------------------------------------------------------------

    private DateOnly Today => MissionPace.Day(_store.Now, _zone);

    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    /// <summary>Reads the store again: the picker, then the selected mission (also "started 2 days ago").</summary>
    public void Refresh()
    {
        try
        {
            RefreshPicker();
            RefreshDetails();
        }
        catch (Exception ex)
        {
            // A refresh comes from a sync on a timer; it must never take the app down.
            _logger.LogError(ex, "Refreshing Missions failed");
        }
    }

    /// <summary>Selects a mission by id (from a link, or after creating it).</summary>
    public void Select(string missionId)
    {
        if (!ShowFinishedMissions && _store.GetMission(missionId) is { Status: MissionStatus.Completed or MissionStatus.Abandoned })
        {
            ShowFinishedMissions = true;
        }
        if (_choices.TryGetValue(missionId, out var choice)) SelectedMission = choice;
    }

    private void RefreshPicker()
    {
        var all = _store.Missions();
        var visible = all.Where(m => ShowFinishedMissions || m.Value.Status is not (MissionStatus.Completed or MissionStatus.Abandoned))
            // In progress first, then planned and paused, then the finished ones; the user's order within each.
            .OrderBy(m => m.Value.Status switch
            {
                MissionStatus.Active => 0,
                MissionStatus.Planned or MissionStatus.Paused => 1,
                MissionStatus.Completed => 2,
                _ => 3,
            })
            .ToList();
        var choices = new List<MissionChoice>();
        foreach (var m in visible)
        {
            if (!_choices.TryGetValue(m.Id, out var choice)) _choices[m.Id] = choice = new MissionChoice(m.Id);
            var steps = _store.Steps(m.Id);
            var done = steps.Count(s => s.Value.IsDone);
            choice.Name = m.Value.Status switch
            {
                MissionStatus.Active => $"{m.Value.Title} · {done}/{steps.Count}",
                MissionStatus.Completed => $"{m.Value.Title} · done",
                _ => $"{m.Value.Title} · {MissionsFormat.Status(m.Value.Status).ToLowerInvariant()}",
            };
            choices.Add(choice);
        }
        foreach (var gone in _choices.Keys.Where(id => all.All(m => m.Id != id)).ToList()) _choices.Remove(gone);

        _loading = true;
        try
        {
            var keep = SelectedMission is { } sel && choices.Contains(sel) ? sel : null;
            SyncCollection(Missions, choices);
            keep ??= _settings.Current.SelectedMissionId is { } saved ? choices.FirstOrDefault(c => c.Id == saved) : null;
            keep ??= choices.FirstOrDefault();
            if (!ReferenceEquals(SelectedMission, keep)) SelectedMission = keep;
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(HasMissions));
        OnPropertyChanged(nameof(HasNoMissions));
    }

    private void RefreshDetails()
    {
        var id = SelectedMission?.Id;
        var mission = id is null ? null : _store.GetMission(id);
        if (id is null || mission is null)
        {
            ClearDetails();
            return;
        }
        var items = _store.Steps(id);
        var steps = items.Select(s => s.Value).ToList();
        var history = _store.History(id);
        var now = _store.Now;
        var today = Today;
        var pace = MissionPace.Compute(mission, steps, history, now, _zone);

        _loading = true;
        try
        {
            Title = mission.Title;
            Goal = mission.Goal;
            Note = mission.Note;
            Status = mission.Status;
            StatusText = MissionsFormat.Status(mission.Status);
            ProgressPercent = pace.Percent;
            ProgressText = mission.Status == MissionStatus.Completed || (pace.Total > 0 && pace.Done == pace.Total)
                ? $"All {Plural(pace.Total, "step")} done"
                : $"Step {Math.Min(pace.Done + 1, pace.Total)} of {pace.Total} · phase {pace.PhaseIndex + 1} of {pace.PhaseCount}";
            PaceText = mission.Status == MissionStatus.Active ? MissionsFormat.Pace(pace) : "";
            IsBehind = mission.Status == MissionStatus.Active && pace.DaysOff > 0;
            ProjectionText = mission.Status == MissionStatus.Active ? MissionsFormat.Projection(pace, mission.Deadline, today) : "";
            DeadlineText = mission.Deadline is { } d ? $"Deadline {MissionsFormat.Date(d, today)}" + DaysLeft(d, today, mission.Status) : "";
            StreakText = mission.Status is MissionStatus.Active ? MissionsFormat.Streak(pace.Streak) : "";
            RewardText = mission.Reward.Length > 0 ? mission.Reward : "";
            CanClaimReward = mission.Status == MissionStatus.Completed && mission.Reward.Length > 0 && mission.RewardClaimedAt is null;
            PlannedText = mission.Status == MissionStatus.Planned
                ? $"{Plural(pace.Total, "step")} in {Plural(mission.Phases.Count, "phase")}, about {MissionsFormat.Days(pace.PlannedDays)}. Started today, it ends around {MissionsFormat.Date(today.AddDays((int)Math.Ceiling(pace.PlannedDays)), today)}."
                : "";
            FinishedText = mission.CompletedAt is { } c && mission.StartedAt is not null
                ? $"Finished {MissionsFormat.Date(c, now, _zone)} after {MissionsFormat.Days(pace.ElapsedDays)} (planned {MissionsFormat.Days(pace.PlannedDays)})." +
                  (mission.RewardClaimedAt is { } r ? $" Reward taken {MissionsFormat.Date(r, now, _zone)}." : "")
                : "";
            CanUndo = mission.Status is MissionStatus.Active or MissionStatus.Completed && steps.Any(s => s.IsDone);
            CanAddStep = mission.Status is MissionStatus.Planned or MissionStatus.Active or MissionStatus.Paused;
            CanSaveToNotes = steps.Any(s => s.IsDone) && NoteTarget() is not null;

            EditTitle = mission.Title;
            EditGoal = mission.Goal;
            EditReward = mission.Reward;
            EditDeadline = mission.Deadline?.ToDateTime(TimeOnly.MinValue);

            RefreshCurrent(mission, items, now);
            RefreshRoadmap(mission, items, now);
        }
        finally
        {
            _loading = false;
        }

        // Finished on another device or by Claude: celebrate once here too (it takes the place of a phase's card).
        if (mission.Status == MissionStatus.Completed && !_settings.Current.CelebratedMissionIds.Contains(id) && Celebration is not { IsMission: true })
        {
            Message = null;
            ShowMissionCelebration(id);
        }
    }

    private static string DaysLeft(DateOnly deadline, DateOnly today, MissionStatus status)
    {
        if (status is MissionStatus.Completed or MissionStatus.Abandoned) return "";
        var left = deadline.DayNumber - today.DayNumber;
        return left switch
        {
            > 1 => $" · {left} days left",
            1 => " · tomorrow",
            0 => " · today",
            -1 => " · 1 day ago",
            _ => $" · {-left} days ago",
        };
    }

    private void RefreshCurrent(Mission mission, IReadOnlyList<SyncedItem<MissionStep>> items, DateTimeOffset now)
    {
        var index = -1;
        for (var i = 0; i < items.Count; i++)
            if (!items[i].Value.IsDone)
            {
                index = i;
                break;
            }
        HasCurrentStep = index >= 0;
        if (index < 0)
        {
            CurrentChecklist.Clear();
            CurrentResources.Clear();
            CurrentTitle = CurrentDescription = CurrentDoneWhen = CurrentStartedText = CurrentPhaseText = CurrentNumberText = "";
            CanStartStep = false;
            NotifyCurrent();
            return;
        }
        var step = items[index].Value;
        var phaseIndex = mission.PhaseIndex(step.PhaseKey);
        CurrentPhaseText = phaseIndex >= 0 ? $"Phase {phaseIndex + 1} of {mission.Phases.Count} · {mission.Phases[phaseIndex].Title}" : "";
        CurrentNumberText = $"Step {index + 1} of {items.Count}";
        CurrentTitle = step.Title;
        CurrentDescription = step.Description;
        CurrentDoneWhen = step.DoneWhen;
        CanStartStep = mission.Status == MissionStatus.Active && !step.StartedExplicitly;
        CurrentStartedText = step.StartedExplicitly && step.StartedAt is { } at
            ? $"Started {Ago(at, now)} · planned {MissionsFormat.Days(step.EstimateDays)}"
            : $"Planned {MissionsFormat.Days(step.EstimateDays)}";

        var rows = step.Checklist.Select((c, i) => (c, i)).ToList();
        if (CurrentChecklist.Count != rows.Count) CurrentChecklist.Clear();
        for (var i = 0; i < rows.Count; i++)
        {
            if (CurrentChecklist.Count <= i) CurrentChecklist.Add(new ChecklistRow(i, OnChecklistChanged));
            CurrentChecklist[i].Load(rows[i].c.Text, rows[i].c.IsDone);
        }
        if (!CurrentResources.Select(r => r.Text).SequenceEqual(step.Resources))
        {
            CurrentResources.Clear();
            foreach (var r in step.Resources) CurrentResources.Add(new ResourceRow(r));
        }
        NotifyCurrent();
    }

    private void NotifyCurrent()
    {
        OnPropertyChanged(nameof(HasCurrentChecklist));
        OnPropertyChanged(nameof(HasCurrentResources));
        OnPropertyChanged(nameof(ShowCurrentStep));
    }

    private void RefreshRoadmap(Mission mission, IReadOnlyList<SyncedItem<MissionStep>> items, DateTimeOffset now)
    {
        var open = mission.Status is MissionStatus.Planned or MissionStatus.Active or MissionStatus.Paused;
        // Before Start, no step is "now" yet.
        var currentId = mission.Status is MissionStatus.Active or MissionStatus.Paused ? items.FirstOrDefault(s => !s.Value.IsDone)?.Id : null;
        var phases = new List<PhaseRow>();
        var number = 0;
        var seenSteps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var phase in mission.Phases)
        {
            var key = $"{SelectedMission!.Id}/{phase.Key}";
            if (!_phaseRows.TryGetValue(key, out var row)) _phaseRows[key] = row = new PhaseRow(phase.Key);
            var inPhase = items.Where(s => s.Value.PhaseKey == phase.Key).ToList();
            var steps = new List<StepRow>();
            foreach (var item in inPhase)
            {
                number++;
                seenSteps.Add(item.Id);
                if (!_stepRows.TryGetValue(item.Id, out var stepRow)) _stepRows[item.Id] = stepRow = new StepRow(item.Id);
                var s = item.Value;
                stepRow.Number = number;
                stepRow.Title = s.Title;
                stepRow.State = s.IsDone ? s.Skipped ? StepState.Skipped : StepState.Done : item.Id == currentId ? StepState.Current : StepState.Upcoming;
                stepRow.Description = s.Description;
                stepRow.DoneWhen = s.DoneWhen;
                stepRow.Note = s.Note;
                stepRow.CanEdit = open && !s.IsDone;
                stepRow.Details = StepDetails(s, stepRow.State, now);
                if (!stepRow.CanEdit) stepRow.IsEditing = false;
                steps.Add(stepRow);
            }
            var done = inPhase.Count(s => s.Value.IsDone);
            row.Title = $"{mission.PhaseIndex(phase.Key) + 1}. {phase.Title}";
            row.IsDone = inPhase.Count > 0 && done == inPhase.Count;
            row.IsCurrent = currentId is not null && inPhase.Any(s => s.Id == currentId);
            var finished = row.IsDone ? inPhase.Max(s => s.Value.CompletedAt) : null;
            row.Details = row.IsDone && finished is { } f
                ? $"Done {MissionsFormat.Date(f, now, _zone)} · {Plural(inPhase.Count, "step")}"
                : $"{done} of {Plural(inPhase.Count, "step")} · about {MissionsFormat.Days(inPhase.Where(s => !s.Value.IsDone).Sum(s => s.Value.EstimateDays))} left";
            row.Reward = phase.Reward;
            row.RewardText = phase.Reward.Length == 0 ? ""
                : phase.RewardClaimedAt is { } claimed ? $"Reward: {phase.Reward} (taken {MissionsFormat.Date(claimed, now, _zone)})"
                : $"Reward: {phase.Reward}";
            row.CanClaimReward = row.IsDone && phase.Reward.Length > 0 && phase.RewardClaimedAt is null;
            SyncCollection(row.Steps, steps);
            phases.Add(row);
        }
        foreach (var gone in _stepRows.Keys.Where(k => !seenSteps.Contains(k) && _store.GetStep(k) is null).ToList()) _stepRows.Remove(gone);
        SyncCollection(Phases, phases);
    }

    private string StepDetails(MissionStep s, StepState state, DateTimeOffset now) => state switch
    {
        StepState.Done => $"Done {MissionsFormat.Date(s.CompletedAt!.Value, now, _zone)}" + (s.StartedAt is { } a ? $" · took {MissionsFormat.Took(s.CompletedAt!.Value - a)}" : ""),
        StepState.Skipped => $"Skipped {MissionsFormat.Date(s.CompletedAt!.Value, now, _zone)}",
        StepState.Current => s.StartedExplicitly && s.StartedAt is { } at ? $"Now · started {Ago(at, now)}" : $"Now · about {MissionsFormat.Days(s.EstimateDays)}",
        _ => $"About {MissionsFormat.Days(s.EstimateDays)}",
    };

    private void ClearDetails()
    {
        Title = Goal = Note = StatusText = ProgressText = PaceText = ProjectionText = DeadlineText = StreakText = RewardText = PlannedText = FinishedText = "";
        ProgressPercent = 0;
        HasCurrentStep = false;
        CanUndo = CanAddStep = CanClaimReward = false;
        CurrentChecklist.Clear();
        CurrentResources.Clear();
        Phases.Clear();
        NotifyCurrent();
    }

    // Derived flags follow their sources.
    partial void OnStatusChanged(MissionStatus value)
    {
        OnPropertyChanged(nameof(IsPlanned));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsAbandoned));
        OnPropertyChanged(nameof(IsOpenForChanges));
        OnPropertyChanged(nameof(ShowCurrentStep));
    }

    partial void OnHasCurrentStepChanged(bool value) => OnPropertyChanged(nameof(ShowCurrentStep));
    partial void OnGoalChanged(string value) => OnPropertyChanged(nameof(HasGoal));
    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
    partial void OnPaceTextChanged(string value) => OnPropertyChanged(nameof(HasPace));
    partial void OnProjectionTextChanged(string value) => OnPropertyChanged(nameof(HasProjection));
    partial void OnDeadlineTextChanged(string value) => OnPropertyChanged(nameof(HasDeadline));
    partial void OnStreakTextChanged(string value) => OnPropertyChanged(nameof(HasStreak));
    partial void OnRewardTextChanged(string value) => OnPropertyChanged(nameof(HasReward));
    partial void OnCurrentDescriptionChanged(string value) => OnPropertyChanged(nameof(HasCurrentDescription));
    partial void OnCurrentDoneWhenChanged(string value) => OnPropertyChanged(nameof(HasCurrentDoneWhen));

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>Makes <paramref name="target"/> hold <paramref name="items"/> in order, touching only what changed.</summary>
    private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> items) where T : class
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], items[i])) continue;
            var existing = target.IndexOf(items[i]);
            if (existing > i) target.Move(existing, i);
            else target.Insert(i, items[i]);
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }

    private string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return span.TotalHours < 2 ? "1 hour ago" : $"{(int)span.TotalHours} hours ago";
        var days = MissionPace.Day(now, _zone).DayNumber - MissionPace.Day(at, _zone).DayNumber;
        return days <= 1 ? "yesterday" : $"{days} days ago";
    }

    internal static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private static bool TryDays(string text, out double days) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out days) && days > 0;
}
