using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Shell.Services;

namespace Helm.Modules.Workspace;

/// <summary>Device-local state of the control screen (which PC, what you have seen).</summary>
public sealed class WorkspaceRemoteSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;
    public int Version { get; set; }
    public string LastMachineId { get; set; } = "";

    /// <summary>Keep watching the PCs while Helm is closed, and notify when a session finishes or waits for you.</summary>
    public bool RunInBackground { get; set; } = true;

    /// <summary>Session id → the update you last saw (a newer answer is "unseen").</summary>
    public Dictionary<string, DateTimeOffset> Seen { get; set; } = new(StringComparer.Ordinal);

    /// <summary>How large the conversation and plans are drawn (1 = normal).</summary>
    public double TextScale { get; set; } = 1;
}

/// <summary>The bottom sheets of the control screen.</summary>
public enum WorkspaceSheet { None, Pc, Model, Mode, Agents, History, Context, TextSize, Status, Attach }

/// <summary>
/// The phone's control screen for Workspace Controller, laid out like Claude's panel in VS Code: a tab per VS Code
/// window of the chosen PC, a subtab per Claude session open in it, the session's timeline as it happens (thoughts,
/// tool calls with IN/OUT, answers), and the composer with model, effort, mode and subagents. Plans wait in their own
/// tab for a decision. Everything goes through Helm Sync; the PC does the typing and reports back.
/// </summary>
public sealed partial class WorkspaceRemoteViewModel : ObservableObject
{
    public const string DefaultModel = "default";

    /// <summary>A command without any answer from its PC after this long is reported as not delivered.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(45);

    private readonly ISyncedCollection<WorkspaceHostRecord> _hosts;
    private readonly ISyncedCollection<WorkspaceSessionRecord> _sessions;
    private readonly ISyncedCollection<WorkspaceCommand> _commands;
    private readonly ISyncedCollection<WorkspaceResultRecord> _results;
    private readonly ISyncedCollection<WorkspaceHistoryRecord>? _history;
    private readonly ISettingsStore<WorkspaceRemoteSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly ISyncService? _sync;
    private readonly IDialogService? _dialogs;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Override> _overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _ultracode = new(StringComparer.Ordinal);
    private string? _shownRequest;
    private bool _refreshing;
    private string? _machineId;
    private string? _folder;
    private string? _sessionId;
    private bool _denyNext;

    /// <summary>
    /// The commands on their way, each with the place it belongs to (its session, or its project for a new session,
    /// a reopened one or a closed tab): a place shows only its own card and status, and the others stay free to use.
    /// </summary>
    private readonly List<Delivery> _deliveries = [];

    /// <summary>The place on screen (project, session or new-session page) whose deliveries are shown.</summary>
    private string? _viewKey;

    /// <summary>Sessions outside VS Code you agreed to go on with from the phone (asked once each).</summary>
    private readonly HashSet<string> _takenOver = new(StringComparer.Ordinal);

    // "Refresh now": when the PC last answered a ping, and the ping on its way.
    private string? _pingId;
    private DateTimeOffset _pingAt;
    private DateTimeOffset? _pcAnsweredAt;
    private TimeSpan? _pcRoundTrip;
    private readonly ITimer _ticker;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _prompt = "";

    [ObservableProperty] private bool _runInBackground;
    [ObservableProperty] private string? _sendStatus;
    [ObservableProperty] private bool _sendFailed;

    /// <summary>Something of the open session (or new-session page) is on its way to the PC; other tabs are not held up.</summary>
    public bool IsSending => _deliveries.Any(d => !d.Sent.Done && d.Sent.Kind is not (WorkspaceCommandKind.Reopen or WorkspaceCommandKind.CloseTab) && Shows(d));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isNewSession;

    /// <summary>The Plan tab instead of the chat (while a plan waits).</summary>
    [ObservableProperty] private bool _showReview;

    /// <summary>"No, keep planning": the next message goes to Claude as what to change in the plan.</summary>
    [ObservableProperty] private bool _planFeedback;

    [ObservableProperty] private WorkspaceSheet _sheet;
    [ObservableProperty] private string _historyQuery = "";

    // A new session's settings, chosen before its first prompt.
    [ObservableProperty] private string _draftModel = DefaultModel;
    [ObservableProperty] private string? _draftEffort;
    [ObservableProperty] private string _draftMode = PermissionModes.Manual;
    [ObservableProperty] private bool _draftUltracode;

    public WorkspaceRemoteViewModel(ISyncedCollection<WorkspaceHostRecord> hosts, ISyncedCollection<WorkspaceSessionRecord> sessions,
        ISyncedCollection<WorkspaceCommand> commands, ISyncedCollection<WorkspaceResultRecord> results, ISettingsStoreFactory settings,
        IUiDispatcher ui, ISyncService? sync = null, TimeProvider? time = null, IDialogService? dialogs = null,
        ISyncedCollection<WorkspaceHistoryRecord>? history = null, BlobStore? blobs = null, IWorkspaceImagePicker? picker = null)
    {
        _blobs = blobs;
        _picker = picker;
        _hosts = hosts;
        _sessions = sessions;
        _commands = commands;
        _results = results;
        _history = history;
        _settings = settings.Get<WorkspaceRemoteSettings>(WorkspaceIds.ModuleId + "-remote");
        _ui = ui;
        _sync = sync;
        _dialogs = dialogs;
        _time = time ?? TimeProvider.System;
        hosts.Changed += (_, _) => _ui.Post(Refresh);
        sessions.Changed += (_, _) => _ui.Post(Refresh);
        results.Changed += (_, _) => _ui.Post(ShowResult);
        if (history is not null) history.Changed += (_, _) => _ui.Post(FillHistory);
        if (sync is not null) sync.StatusChanged += (_, _) => _ui.Post(() =>
        {
            foreach (var d in _deliveries) StepsOf(d);
            RebuildProgress();
            if (_deliveries.Any(Shows)) FillTimeline();
            NotifyFreshness();
        });
        // Once a second while the screen is shown: how long things take, and how fresh what you see is.
        _ticker = _time.CreateTimer(_ => _ui.Post(Tick), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _runInBackground = _settings.Current.RunInBackground;
        _machineId = _settings.Current.LastMachineId is { Length: > 0 } last ? last : null;
        Refresh();
    }

    public ObservableCollection<HostChoice> Hosts { get; } = [];
    public ObservableCollection<ProjectTab> Projects { get; } = [];
    public ObservableCollection<SessionTab> SessionTabs { get; } = [];

    /// <summary>
    /// The open conversation, oldest first (the view keeps the end in sight, as VS Code does): its last
    /// <see cref="TimelinePage"/> steps, more with <see cref="ShowEarlierCommand"/>. A new list when another session
    /// opens (one redraw), changed in place while the same one goes on.
    /// </summary>
    public ObservableCollection<TimelineRow> Timeline { get; private set; } = [];

    /// <summary>Steps drawn at first; older ones come on request (a phone draws a long timeline slowly).</summary>
    public const int TimelinePage = 30;

    private int _timelineLimit = TimelinePage;
    private string? _timelineFor;
    private readonly Dictionary<string, ObservableCollection<TimelineRow>> _timelines = new(StringComparer.Ordinal);
    private bool _deferFill;

    /// <summary>The timelines kept for the open tabs (the view keeps a drawn list for each: switching back costs nothing).</summary>
    public IReadOnlyCollection<ObservableCollection<TimelineRow>> CachedTimelines => _timelines.Values;

    /// <summary>
    /// Set by the view: runs work after the next frame. A tab switch shows the new tab at once and fills its timeline
    /// right after, instead of holding the screen while the rows are made.
    /// </summary>
    public Action<Action>? DeferTimeline { get; set; }

    /// <summary>Older steps not drawn yet.</summary>
    public int HiddenSteps { get; private set; }
    public bool HasHiddenSteps => HiddenSteps > 0;
    public string HiddenStepsText => HiddenSteps == 1 ? "Show 1 earlier step" : $"Show {Math.Min(HiddenSteps, TimelinePage)} earlier steps";

    [RelayCommand]
    private void ShowEarlier()
    {
        _timelineLimit += TimelinePage;
        FillTimeline();
    }

    /// <summary>Tabs closed from the phone: gone at once, back only if the PC could not close them.</summary>
    private readonly HashSet<string> _closing = new(StringComparer.Ordinal);

    public ObservableCollection<SkillInfo> Suggestions { get; } = [];

    /// <summary>The questions of a held AskUserQuestion of the open session.</summary>
    public ObservableCollection<QuestionChoice> Questions { get; } = [];

    public ObservableCollection<ModelOption> ModelOptions { get; } = [];
    public ObservableCollection<ModeOption> ModeOptions { get; } = [];
    public ObservableCollection<EffortDot> EffortDots { get; } = [];
    public ObservableCollection<AgentRow> AgentRows { get; } = [];
    public ObservableCollection<HistoryRow> HistoryRows { get; } = [];

    /// <summary>The steps of the command on its way, for the progress card.</summary>
    public ObservableCollection<ProgressStep> ProgressSteps { get; } = [];

    /// <summary>What the PC is busy with for the phone (the status sheet).</summary>
    public ObservableCollection<string> PcActivities { get; } = [];

    // ---- The PC

    public HostChoice? SelectedHost => Hosts.FirstOrDefault(h => h.MachineId == _machineId);
    public bool HasHosts => Hosts.Count > 0;
    public bool HostOnline => SelectedHost?.IsOnline == true;
    public string HostName => SelectedHost?.Host.MachineName ?? "No PC";

    public string HostStatus => SelectedHost switch
    {
        null => "",
        { IsOnline: true } h => h.Detail,
        { } h => $"{h.Detail}. Turn on Workspace Controller in Helm on that PC.",
    };

    // ---- Tabs

    public ProjectTab? SelectedProject => Projects.FirstOrDefault(p => p.IsSelected);
    public bool HasProjects => Projects.Count > 0;
    public SessionTab? SelectedTab => IsNewSession ? null : SessionTabs.FirstOrDefault(t => t.IsSelected);
    public WorkspaceSessionRecord? Session => SelectedTab?.Record;
    public bool IsSessionOpen => Session is not null;

    /// <summary>The conversation open on screen now (no notification for it).</summary>
    public string? OpenSessionId => Session?.SessionId;

    // ---- The session header

    public string SessionTitle => IsNewSession ? "New session" : SelectedTab?.Title ?? "";
    public string SessionStateText => Session is { } r ? Describe(r) + (r.Hosted ? " · Helm runs it on the PC" : "") : IsNewSession && SelectedProject is { } p ? $"Opens a Claude tab in {p.Name}" : "";

    // ---- Outside VS Code

    /// <summary>The open tab lists sessions outside VS Code: no new session or History there.</summary>
    public bool IsOutsideProject => SelectedProject?.IsOutside == true;
    public bool CanStartNew => SelectedProject is { IsOutside: false };

    /// <summary>The open session is outside VS Code (Helm runs it on the PC when you send it something).</summary>
    public bool IsOutsideSession => Session?.IsOutside == true;

    /// <summary>Helm runs the open session in the background and it is at work: it can be stopped from here.</summary>
    public bool CanStop => HostOnline && Session is { Hosted: true, State: SessionState.Working or SessionState.NeedsPermission or SessionState.Question };
    public bool IsWorking => Session?.State == SessionState.Working;
    public bool NeedsYou => Session?.State is SessionState.NeedsPermission or SessionState.Question;

    internal static string Describe(WorkspaceSessionRecord r) => r.State switch
    {
        SessionState.Idle => "Ready",
        SessionState.Working => r.WorkingSince is { } since ? $"Working since {since.ToLocalTime():HH:mm}" : "Working",
        SessionState.NeedsPermission when r.Pending?.Kind == PendingKind.Plan => "Plan ready for you",
        SessionState.NeedsPermission => "Needs your permission" + (r.Waiting is { Length: > 0 } w ? $": {w}" : ""),
        SessionState.Question => "Asked you a question",
        SessionState.Done => $"Done at {r.UpdatedAt.ToLocalTime():HH:mm}",
        SessionState.Ended => "Ended",
        _ => "",
    };

    // ---- What Claude waits for

    /// <summary>What the PC holds for you in the open session; null when nothing.</summary>
    public PendingRequest? Pending => Session?.Pending;
    public bool HasPermission => Pending?.Kind == PendingKind.Permission;
    public bool HasQuestion => Pending?.Kind == PendingKind.Question;
    public bool HasPlan => Pending?.Kind == PendingKind.Plan;
    public string PendingTitle => Pending is { } p ? $"Allow {p.Tool}?" : "";
    public string PendingDetail => Pending?.Detail ?? "";
    public string PendingExpires => Pending is { } p ? $"Held for your phone until {p.ExpiresAt.ToLocalTime():HH:mm}, then VS Code asks on the PC." : "";

    /// <summary>The plan, ready to draw.</summary>
    public IReadOnlyList<MarkdownBlock> PlanBlocks { get; private set; } = [];

    /// <summary>A review waits (only plans for now): the header shows "Chat | Plan".</summary>
    public bool HasReview => HasPlan;
    public string ReviewLabel => HasPlan ? "Plan" : "";
    public bool ShowChat => !ShowReview;

    // ---- The composer's chips

    private WorkspaceHostRecord? Host => SelectedHost?.Host;

    private IReadOnlyList<ModelInfo> ModelList => Host?.ModelChoices is { Count: > 0 } list ? list :
        [.. new[] { DefaultModel }.Concat(Host?.Models ?? []).Select(m => new ModelInfo { Value = m, DisplayName = m == DefaultModel ? "Default" : char.ToUpperInvariant(m[0]) + m[1..] })];

    /// <summary>The model the open session (or the new one) runs, as its list entry when known.</summary>
    private ModelInfo? CurrentModel
    {
        get
        {
            var list = ModelList;
            if (IsNewSession) return list.FirstOrDefault(m => m.Value == DraftModel) ?? list.FirstOrDefault();
            if (Session is not { } s) return null;
            if (OverrideOf(s, o => o.Model) is { } picked) return list.FirstOrDefault(m => m.Value == picked);
            // Several entries can run the same model (Default and Opus 5.5): the named one, not "Default".
            return s.Model is { } id ? list.Where(m => m.ResolvedModel == id).OrderBy(m => m.Value == DefaultModel).FirstOrDefault() : null;
        }
    }

    public string ModelLabel => CurrentModel is { } m ? ShortName(m) : Session?.Model is { } id ? id.Replace("claude-", "") : "Model";

    /// <summary>
    /// The chip's name of a model, with its version as VS Code shows it ("Opus 5.5"): the start of the description
    /// names it for "Default" and for lists whose names carry no version ("Opus 5.5 with 1M context · …").
    /// </summary>
    internal static string ShortName(ModelInfo m)
    {
        if (m.Value != DefaultModel && m.DisplayName.Any(char.IsDigit)) return m.DisplayName;
        var lead = m.Description.Split(" · ")[0];
        var with = lead.IndexOf(" with ", StringComparison.Ordinal);
        if (with > 0) lead = lead[..with];
        return lead.Length is > 0 and <= 24 && lead.Any(char.IsDigit) ? lead : m.DisplayName;
    }

    private string? CurrentEffort => IsNewSession ? DraftEffort : Session is { } s ? OverrideOf(s, o => o.Effort) ?? s.Effort : null;
    public string EffortLabel => IsUltracode ? "Ultracode" : PermissionModes.EffortLabel(CurrentEffort) switch { "Extra high" => "xHigh", var l => l };
    public bool HasEffort => EffortLabel.Length > 0;

    public bool IsUltracode => IsNewSession ? DraftUltracode : Session is { } s && _ultracode.GetValueOrDefault(s.SessionId);

    private string CurrentMode => IsNewSession ? DraftMode : PermissionModes.Normalize(Session is { } s ? OverrideOf(s, o => o.Mode) ?? s.PermissionMode : null);
    public string ModeName => PermissionModes.Label(CurrentMode);
    public string ModeKey => CurrentMode;
    public bool ModeIsManual => CurrentMode == PermissionModes.Manual;
    public bool ModeIsEdits => CurrentMode == PermissionModes.EditAutomatically;
    public bool ModeIsPlan => CurrentMode == PermissionModes.Plan;
    public bool ModeIsAuto => CurrentMode is not (PermissionModes.Manual or PermissionModes.EditAutomatically or PermissionModes.Plan);

    public double ContextFraction => Session is { ContextWindow: > 0 } s ? Math.Clamp((double)s.ContextTokens / s.ContextWindow, 0, 1) : 0;
    public string ContextText => Session is { ContextWindow: > 0 } s
        ? $"{Tokens(s.ContextTokens)} of {Tokens(s.ContextWindow)} tokens of context used ({ContextFraction:P0}). /compact frees it up."
        : "No context used yet.";

    public string AgeText => Session is { } s && s.StartedAt != default ? Short(_time.GetLocalNow() - s.StartedAt) : "0m";
    public int AgentCount => Session?.Agents.Count ?? 0;
    public string AgentsText => AgentCount == 1 ? "1 agent" : $"{AgentCount} agents";
    public bool HasAgents => AgentCount > 0;

    /// <summary>The last command's line shows above the composer when the timeline does not show it (a model switch, a tab closed).</summary>
    public bool ShowStatus => SendStatus is { Length: > 0 } && !ShowProgress && Timeline.LastOrDefault()?.HasDelivery != true && !Timeline.Any(r => r.IsStep);

    /// <summary>The PC's account offers Auto mode (a plan can be approved into it).</summary>
    public bool AutoModeAvailable => Host?.AutoModeAvailable == true;

    partial void OnSendStatusChanged(string? value) => Notify(nameof(ShowStatus));

    public bool CanType => HostOnline && (IsNewSession ? SelectedProject is not null : Session is not null);

    public string PromptHint => !HasHosts ? "No PC yet"
        : !HostOnline ? $"{HostName} is offline"
        : IsNewSession ? $"New session in {SelectedProject?.Name}…"
        : PlanFeedback ? "Tell Claude what to change in the plan…"
        : _denyNext ? "Tell Claude what to do instead…"
        : IsWorking ? "Queue another message…"
        : IsOutsideSession && Session?.Hosted != true ? "Go on with this session from the phone…"
        : "Ask Claude to edit…";

    // ---- Sheets

    public bool IsSheetOpen => Sheet != WorkspaceSheet.None;
    public bool IsPcSheet => Sheet == WorkspaceSheet.Pc;
    public bool IsModelSheet => Sheet == WorkspaceSheet.Model;
    public bool IsModeSheet => Sheet == WorkspaceSheet.Mode;
    public bool IsAgentsSheet => Sheet == WorkspaceSheet.Agents;
    public bool IsHistorySheet => Sheet == WorkspaceSheet.History;
    public bool IsContextSheet => Sheet == WorkspaceSheet.Context;
    public bool IsTextSizeSheet => Sheet == WorkspaceSheet.TextSize;
    public bool IsStatusSheet => Sheet == WorkspaceSheet.Status;

    // ---- Text size

    /// <summary>The sizes offered, as a share of normal.</summary>
    public static IReadOnlyList<double> TextScales { get; } = [0.85, 1, 1.15, 1.3, 1.5, 1.75];

    /// <summary>How large the conversation and plans are drawn (saved on this phone).</summary>
    public double TextScale => Math.Clamp(_settings.Current.TextScale is > 0 and var t ? t : 1, TextScales[0], TextScales[^1]);
    public string TextScaleLabel => $"{TextScale:P0}";

    [RelayCommand] private void LargerText() => SetTextScale(TextScales.FirstOrDefault(t => t > TextScale + 0.001, TextScales[^1]));
    [RelayCommand] private void SmallerText() => SetTextScale(TextScales.LastOrDefault(t => t < TextScale - 0.001, TextScales[0]));
    [RelayCommand] private void ResetText() => SetTextScale(1);

    private void SetTextScale(double scale)
    {
        if (Math.Abs(scale - TextScale) < 0.001) return;
        _settings.Update(s => s.TextScale = scale);
        Notify(nameof(TextScale), nameof(TextScaleLabel));
    }
    public bool ShowsEffort => IsModelSheet || IsModeSheet;
    public string HistoryTitle => SelectedProject is { } p ? $"History · {p.Name}" : "History";
    public string EffortHeading => $"Effort ({(PermissionModes.EffortLabel(CurrentEffort) is { Length: > 0 } e ? e : "default")})";

    partial void OnSheetChanged(WorkspaceSheet value)
    {
        if (value == WorkspaceSheet.History) FillHistory();
        if (value is WorkspaceSheet.Model or WorkspaceSheet.Mode) FillChoices();
        if (value == WorkspaceSheet.Agents) FillAgents();
        if (value == WorkspaceSheet.Status) NotifyFreshness();
        Notify(nameof(IsSheetOpen), nameof(IsPcSheet), nameof(IsModelSheet), nameof(IsModeSheet), nameof(IsAgentsSheet), nameof(IsHistorySheet),
            nameof(IsContextSheet), nameof(IsTextSizeSheet), nameof(IsStatusSheet), nameof(IsAttachSheet), nameof(ShowsEffort));
    }

    partial void OnHistoryQueryChanged(string value) => FillHistory();
    partial void OnShowReviewChanged(bool value) => Notify(nameof(ShowChat));
    partial void OnPlanFeedbackChanged(bool value) => Notify(nameof(PromptHint));
    partial void OnIsNewSessionChanged(bool value) => NotifySession();

    partial void OnRunInBackgroundChanged(bool value)
    {
        if (_settings.Current.RunInBackground != value) _settings.Update(s => s.RunInBackground = value);
    }

    partial void OnPromptChanged(string value)
    {
        Suggestions.Clear();
        var t = value.TrimStart();
        if (t.StartsWith('/') && !t.Contains(' ') && Host is { } h)
        {
            var typed = t[1..];
            foreach (var skill in h.Skills.Where(s => s.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Take(8))
                Suggestions.Add(skill);
        }
        Notify(nameof(HasSuggestions));
    }

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>The settings of this screen (the phone module reads RunInBackground).</summary>
    public ISettingsStore<WorkspaceRemoteSettings> Settings => _settings;

    // ---- Navigation

    [RelayCommand]
    private void SelectHost(HostChoice? host)
    {
        Sheet = WorkspaceSheet.None;
        if (host is null || host.MachineId == _machineId) return;
        _machineId = host.MachineId;
        _settings.Update(s => s.LastMachineId = host.MachineId);
        _folder = null;
        _sessionId = null;
        IsNewSession = false;
        Refresh();
    }

    [RelayCommand]
    private void SelectProject(ProjectTab? project)
    {
        if (project is null) return;
        _deferFill = true;
        _folder = project.Folder;
        _sessionId = null;
        IsNewSession = false;
        ShowReview = false;
        Refresh();
    }

    [RelayCommand]
    private void SelectSession(SessionTab? tab)
    {
        if (tab is null) return;
        _deferFill = true;
        _sessionId = tab.SessionId;
        IsNewSession = false;
        ShowReview = false;
        PlanFeedback = false;
        _denyNext = false;
        Refresh();
    }

    [RelayCommand]
    private void NewSession()
    {
        if (SelectedProject is not { IsOutside: false }) return;
        IsNewSession = true;
        ShowReview = false;
        PlanFeedback = false;
        Sheet = WorkspaceSheet.None;
        Refresh();
    }

    [RelayCommand]
    private void CloseTab(SessionTab? tab)
    {
        if (tab is null || !HostOnline) return;
        // Gone from the strip now; the neighbour opens. VS Code closes it a moment later.
        _closing.Add(tab.SessionId);
        Write(new WorkspaceCommand { MachineId = _machineId!, Kind = WorkspaceCommandKind.CloseTab, SessionId = tab.SessionId, CreatedAt = _time.GetLocalNow() },
            "Closing the tab in VS Code…");
        Refresh();
    }

    [RelayCommand] private void ShowChatView() => ShowReview = false;
    [RelayCommand] private void ShowReviewView() => ShowReview = HasReview;

    [RelayCommand]
    private void OpenSheet(string? kind)
    {
        if (Enum.TryParse<WorkspaceSheet>(kind, ignoreCase: true, out var sheet)) Sheet = sheet;
    }

    [RelayCommand] private void CloseSheet() => Sheet = WorkspaceSheet.None;

    /// <summary>Opens one session (from a notification): its PC, its window's tab, the conversation.</summary>
    public void OpenSession(string machineId, string sessionId)
    {
        if (_sessions.Get(sessionId) is not { } record) return;
        _machineId = machineId.Length > 0 ? machineId : record.MachineId;
        _folder = record.WindowFolder;
        _sessionId = sessionId;
        IsNewSession = false;
        Refresh();
    }

    /// <summary>Android back: closes a sheet or the review first. False when there is nothing to go back from.</summary>
    public bool HandleBack()
    {
        if (ViewedImage is not null) { ViewedImage = null; return true; }
        if (Sheet != WorkspaceSheet.None) { Sheet = WorkspaceSheet.None; return true; }
        if (ShowReview) { ShowReview = false; return true; }
        return false;
    }

    /// <summary>Kept for the page of 0.42.</summary>
    public bool CloseSession() => HandleBack();

    [RelayCommand]
    private void PickSuggestion(SkillInfo? skill)
    {
        if (skill is not null) Prompt = "/" + skill.Name + " ";
    }

    // ---- Settings of a session

    [RelayCommand]
    private void SetModel(ModelOption? option)
    {
        Sheet = WorkspaceSheet.None;
        if (option is null) return;
        if (IsNewSession) { DraftModel = option.Model.Value; NotifyChips(); return; }
        if (Session is not { } s || !HostOnline) return;
        SetOverride(s, o => o with { Model = option.Model.Value, ModelBase = s.Model });
        Write(new WorkspaceCommand { MachineId = _machineId!, Kind = WorkspaceCommandKind.SetModel, SessionId = s.SessionId, Text = option.Model.Value, CreatedAt = _time.GetLocalNow() },
            $"Switching to {option.Name}…");
    }

    [RelayCommand]
    private void SetEffort(EffortDot? dot)
    {
        if (dot is null || !dot.IsAvailable) return;
        if (IsNewSession) { DraftEffort = dot.Level; NotifyChips(); FillChoices(); return; }
        if (Session is not { } s || !HostOnline) return;
        SetOverride(s, o => o with { Effort = dot.Level, EffortBase = s.Effort });
        FillChoices();
        Write(new WorkspaceCommand { MachineId = _machineId!, Kind = WorkspaceCommandKind.SetEffort, SessionId = s.SessionId, Text = dot.Level, CreatedAt = _time.GetLocalNow() },
            $"Effort {dot.Label}…");
    }

    [RelayCommand]
    private void ToggleUltracode()
    {
        if (IsNewSession) { DraftUltracode = !DraftUltracode; NotifyChips(); return; }
        if (Session is not { } s || !HostOnline) return;
        var on = !_ultracode.GetValueOrDefault(s.SessionId);
        _ultracode[s.SessionId] = on;
        NotifyChips();
        Write(new WorkspaceCommand { MachineId = _machineId!, Kind = WorkspaceCommandKind.SetEffort, SessionId = s.SessionId, Text = on ? "ultracode" : "ultracode off", CreatedAt = _time.GetLocalNow() },
            on ? "Turning Ultracode on…" : "Turning Ultracode off…");
    }

    [RelayCommand]
    private void SetMode(ModeOption? option)
    {
        Sheet = WorkspaceSheet.None;
        if (option is null) return;
        if (IsNewSession) { DraftMode = option.Mode; NotifyChips(); return; }
        if (Session is not { } s || !HostOnline || option.Mode == CurrentMode) return;
        SetOverride(s, o => o with { Mode = option.Mode, ModeBase = s.PermissionMode });
        Write(new WorkspaceCommand { MachineId = _machineId!, Kind = WorkspaceCommandKind.SetMode, SessionId = s.SessionId, Text = option.Mode, CreatedAt = _time.GetLocalNow() },
            $"Switching to {option.Name}…");
    }

    [RelayCommand]
    private void OpenHistory(HistoryRow? row)
    {
        if (row is null || row.IsDay) return;
        Sheet = WorkspaceSheet.None;
        if (row.IsOpen)
        {
            SelectSession(SessionTabs.FirstOrDefault(t => t.SessionId == row.SessionId));
            return;
        }
        if (!HostOnline || SelectedProject is not { IsOutside: false } project) return;
        Write(new WorkspaceCommand { MachineId = _machineId!, Kind = WorkspaceCommandKind.Reopen, SessionId = row.SessionId, Folder = project.Folder, CreatedAt = _time.GetLocalNow() },
            "Opening it in VS Code…", title: $"Opening “{row.Title}”", awaitSession: row.SessionId);
    }

    /// <summary>Stops the session Helm runs on the PC (outside VS Code) where it is.</summary>
    [RelayCommand]
    private void Stop()
    {
        if (!CanStop || Session is not { } s) return;
        Write(new WorkspaceCommand { MachineId = _machineId!, Kind = WorkspaceCommandKind.Stop, SessionId = s.SessionId, CreatedAt = _time.GetLocalNow() }, "Stopping Claude on the PC…");
    }

    // ---- Answers

    private bool CanAnswer() => !IsSending && HostOnline && Pending is not null;

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private async Task AllowAsync()
    {
        if (Pending is not { } p) return;
        // Allowing runs something on the PC: say exactly what, once more.
        if (_dialogs is not null && !await _dialogs.ConfirmAsync($"Allow {p.Tool}?", p.Detail.Length > 0 ? p.Detail : "Claude will run it on the PC.", "Allow").ConfigureAwait(true)) return;
        if (Pending?.RequestId != p.RequestId) return;
        WriteAnswer(p, allow: true, answers: null);
    }

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void Deny()
    {
        if (Pending is { } p) WriteAnswer(p, allow: false, answers: null, message: Prompt.Trim());
    }

    /// <summary>"No, and tell Claude what to do instead": the next message is the reason.</summary>
    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void TellInstead()
    {
        _denyNext = true;
        Notify(nameof(PromptHint));
    }

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void SendAnswers()
    {
        if (Pending is not { Kind: PendingKind.Question } p) return;
        if (Questions.FirstOrDefault(q => q.Answer is null) is { } missing)
        {
            SendFailed = true;
            SendStatus = $"Answer “{missing.Question}” first.";
            return;
        }
        WriteAnswer(p, allow: true, Questions.ToDictionary(q => q.Question, q => q.Answer!));
    }

    /// <summary>Approves the plan; Claude goes on in <paramref name="mode"/> (acceptEdits, default, auto).</summary>
    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void ApprovePlan(string? mode)
    {
        if (Pending is not { Kind: PendingKind.Plan } p) return;
        ShowReview = false;
        var m = PermissionModes.Normalize(mode);
        if (Session is { } s) SetOverride(s, o => o with { Mode = m, ModeBase = s.PermissionMode });
        WriteAnswer(p, allow: true, answers: null, planMode: m);
    }

    /// <summary>"No, keep planning": back to the chat, where the next message says what to change.</summary>
    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private void KeepPlanning()
    {
        ShowReview = false;
        PlanFeedback = true;
    }

    private void WriteAnswer(PendingRequest p, bool allow, Dictionary<string, string>? answers, string message = "", string? planMode = null)
    {
        if (message.Length > 0) Prompt = "";
        _denyNext = false;
        PlanFeedback = false;
        Write(new WorkspaceCommand
        {
            MachineId = _machineId!,
            Kind = WorkspaceCommandKind.Answer,
            SessionId = p.SessionId,
            RequestId = p.RequestId,
            Allow = allow,
            Answers = answers,
            PlanMode = planMode,
            Text = message,
            CreatedAt = _time.GetLocalNow(),
        });
    }

    // ---- Sending

    private bool CanSend() => !IsSending && CanType && (!string.IsNullOrWhiteSpace(Prompt) || Attachments.Count > 0 && ImagesSupported && !PlanFeedback && !_denyNext);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Prompt.Trim();
        // Outside VS Code Helm runs the session itself: say what that means, once per session.
        if (!IsNewSession && Session is { IsOutside: true, Hosted: false } outside && outside.State != SessionState.Ended && !_takenOver.Contains(outside.SessionId))
        {
            if (_dialogs is not null && !await _dialogs.ConfirmAsync("Go on with it from the phone?",
                    "Helm runs this session on the PC in the background, with the same conversation. If it is still open in a terminal, that terminal will not see these messages; close it there first.",
                    "Go on").ConfigureAwait(true)) return;
            _takenOver.Add(outside.SessionId);
            if (Session?.SessionId != outside.SessionId) return;
        }
        if (PlanFeedback && Pending is { Kind: PendingKind.Plan } plan)
        {
            WriteAnswer(plan, allow: false, answers: null, message: text);
            return;
        }
        if (_denyNext && Pending is { Kind: PendingKind.Permission } held)
        {
            WriteAnswer(held, allow: false, answers: null, message: text);
            return;
        }
        if (IsNewSession)
        {
            Write(new WorkspaceCommand
            {
                MachineId = _machineId!,
                Kind = WorkspaceCommandKind.NewSession,
                Folder = SelectedProject!.Folder,
                Text = text,
                Model = DraftModel == DefaultModel ? null : DraftModel,
                Effort = DraftUltracode ? "ultracode" : DraftEffort,
                Mode = DraftMode == PermissionModes.Manual ? null : DraftMode,
                Images = ImagesSupported ? TakeAttachments() : [],
                CreatedAt = _time.GetLocalNow(),
            }, sending: text, title: $"Starting a new session in {SelectedProject!.Name}");
            return;
        }
        Write(new WorkspaceCommand
        {
            MachineId = _machineId!,
            Kind = WorkspaceCommandKind.Prompt,
            SessionId = Session!.SessionId,
            Folder = Session.Folder,
            Text = text,
            Images = ImagesSupported ? TakeAttachments() : [],
            CreatedAt = _time.GetLocalNow(),
        }, sending: text, title: Session!.IsOutside ? "Sending it to Claude on the PC" : "Sending your message");
    }

    private void Write(WorkspaceCommand command, string status = "Sending to the PC…", string? sending = null, string? title = null, string? awaitSession = null)
    {
        _commands.Upsert(command.Id, command);
        _sync?.RequestSyncSoon();
        var delivery = new Delivery(new SentCommand(command.Id, command.Kind, command.SessionId, sending, _time.GetLocalNow()) { Images = command.Images },
            _folder, title, awaitSession) { Status = status };
        // What finished here before makes room for it (other tabs keep theirs).
        _deliveries.RemoveAll(d => d.Sent.Done && d.AwaitSession is null && Shows(d));
        _deliveries.Add(delivery);
        SendFailed = false;
        SendStatus = status;
        if (sending is not null || command.Kind == WorkspaceCommandKind.Answer) Prompt = "";
        _ = WatchTimeoutAsync(command.Id);
        NotifySending();
        RebuildProgress();
        FillTimeline();
    }

    private async Task WatchTimeoutAsync(string id)
    {
        await Task.Delay(AnswerTimeout).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (_deliveries.Find(d => d.Sent.CommandId == id) is not { Sent.Done: false } d || _results.Get(id) is not null) return;
            Finish(d, failed: true, "The PC did not answer. Is Helm running there, with Workspace Controller on?");
            NotifySending();
            RebuildProgress();
            FillTimeline();
        });
    }

    private void ShowResult()
    {
        if (_pingId is { } ping && _results.Get(ping) is { Status: not WorkspaceCommandStatus.Sending })
        {
            _pingId = null;
            _pcAnsweredAt = _time.GetLocalNow();
            _pcRoundTrip = _pcAnsweredAt - _pingAt;
            NotifyFreshness();
        }
        var changed = false;
        foreach (var d in _deliveries.Where(d => !d.Sent.Done).ToList())
        {
            if (_results.Get(d.Sent.CommandId) is not { } result) continue;
            changed = true;
            var sent = d.Sent;
            if (result.Status == WorkspaceCommandStatus.Sending && result.Step is { Length: > 0 } step && (d.PcSteps.Count == 0 || d.PcSteps[^1] != step)) d.PcSteps.Add(step);
            StepsOf(d);
            var failed = result.Status == WorkspaceCommandStatus.Failed;
            var status = result.Status switch
            {
                WorkspaceCommandStatus.Sending => sent.Text is null ? d.Status : "The PC is typing it into VS Code…",
                WorkspaceCommandStatus.Sent => result.Error ?? (sent.Text is null ? "Done on the PC." : "Typed into VS Code."),
                WorkspaceCommandStatus.Received => sent.Text is null ? "Done." : "✓ Claude received it",
                _ => result.Error ?? "It could not be sent.",
            };
            if (result.Status == WorkspaceCommandStatus.Sending)
            {
                d.Status = status;
                if (d == Latest) { SendStatus = status; SendFailed = false; }
                continue;
            }
            // A new session: open it once its tab shows up.
            if (sent.Kind == WorkspaceCommandKind.NewSession && result.Status == WorkspaceCommandStatus.Received && result.SessionId is { } sid) d.AwaitSession = sid;
            Finish(d, failed, status);
            StepsOf(d);
            if (failed) RevertOverride(sent);
            // VS Code kept the tab: it comes back.
            if (failed && sent.Kind == WorkspaceCommandKind.CloseTab && sent.SessionId is { } kept) _closing.Remove(kept);
        }
        if (!changed) return;
        NotifySending();
        Refresh();
        RebuildProgress();
    }

    /// <summary>
    /// The PC answered (or never did). Shown where it belongs; one that went well while you were in another tab is
    /// put away, one that failed waits for you there.
    /// </summary>
    private void Finish(Delivery d, bool failed, string? status)
    {
        d.Sent = d.Sent with { Done = true, Failed = failed };
        d.Status = status;
        d.Failed = failed;
        d.AnsweredAt = _time.GetLocalNow();
        d.SeenDone = Shows(d);
        if (failed && d.Sent.Kind == WorkspaceCommandKind.Reopen) d.AwaitSession = null;
        if (d == Latest)
        {
            SendFailed = failed;
            SendStatus = status;
        }
        else if (!failed && d.AwaitSession is null && !Shows(d)) _deliveries.Remove(d);
    }

    private void NotifySending()
    {
        Notify(nameof(IsSending));
        SendCommand.NotifyCanExecuteChanged();
        AllowCommand.NotifyCanExecuteChanged();
        DenyCommand.NotifyCanExecuteChanged();
        TellInsteadCommand.NotifyCanExecuteChanged();
        SendAnswersCommand.NotifyCanExecuteChanged();
        ApprovePlanCommand.NotifyCanExecuteChanged();
        KeepPlanningCommand.NotifyCanExecuteChanged();
    }

    // ---- Where a delivery shows

    /// <summary>The place on screen now: the new-session page of a project, or one of its sessions.</summary>
    private string ViewKey => (IsNewSession ? "new:" : "session:" + Session?.SessionId + ":") + _folder;

    /// <summary>
    /// Whether <paramref name="d"/> belongs to what is on screen: a new session to its project's new-session page,
    /// a reopened session or a closed tab to its project, anything else to its session.
    /// </summary>
    private bool Shows(Delivery d) => d.Sent.Kind switch
    {
        WorkspaceCommandKind.NewSession => IsNewSession && SameFolder(d.Folder, _folder),
        WorkspaceCommandKind.Reopen or WorkspaceCommandKind.CloseTab => SameFolder(d.Folder, _folder),
        _ => !IsNewSession && d.Sent.SessionId is { } id && id == Session?.SessionId,
    };

    /// <summary>The latest delivery of the place on screen: its line above the composer.</summary>
    private Delivery? Latest => _deliveries.LastOrDefault(Shows);

    /// <summary>The prompt, new session or reopened session of the place on screen: its progress card.</summary>
    private Delivery? Card => _deliveries.LastOrDefault(d => d.Sent.Kind is WorkspaceCommandKind.Prompt or WorkspaceCommandKind.NewSession or WorkspaceCommandKind.Reopen && Shows(d));

    /// <summary>
    /// Another place opened: what you saw finish where you were is put away, then the new place shows its own
    /// (a message still on its way there, or one that failed while you were elsewhere).
    /// </summary>
    private void FollowView()
    {
        var key = ViewKey;
        if (key == _viewKey) return;
        _viewKey = key;
        _deliveries.RemoveAll(d => d.Sent.Done && d.AwaitSession is null && d.SeenDone && !Shows(d));
        foreach (var d in _deliveries.Where(d => d.Sent.Done && Shows(d))) d.SeenDone = true;
        var latest = Latest;
        SendStatus = latest?.Status;
        SendFailed = latest?.Failed ?? false;
        NotifySending();
    }

    // ---- The progress card

    /// <summary>A prompt, a new session or a session opened from History is on its way (or just failed): the card shows each step.</summary>
    public bool ShowProgress => Card is { } d && (!d.Sent.Done || d.Sent.Failed || AwaitingTab(d));

    /// <summary>The PC is done; the new or reopened tab has not shown up on the phone yet.</summary>
    private static bool AwaitingTab(Delivery d) => d.AwaitSession is not null && d.Sent is { Done: true, Failed: false, Kind: WorkspaceCommandKind.NewSession or WorkspaceCommandKind.Reopen } && !d.AwaitGaveUp;

    public bool ProgressFailed => Card is { Sent.Failed: true } or { AwaitGaveUp: true };
    public bool ProgressRunning => ShowProgress && !ProgressFailed;
    public string ProgressTitle => Card?.Title ?? "Sending to the PC";

    /// <summary>"12 s": how long it has been on its way.</summary>
    public string ProgressElapsed => Card is { } d ? Seconds(_time.GetLocalNow() - d.Sent.At) : "";

    /// <summary>Said when it takes longer than it should, with what may hold it up.</summary>
    public string ProgressHint => Card is { } d ? HintOf(d) : "";

    public bool HasProgressHint => ProgressHint.Length > 0;

    /// <summary>The card's steps (the timeline shows them on its rail): sent from the phone, picked up by the PC, each step there, then Claude has it.</summary>
    private void RebuildProgress()
    {
        var steps = Card is { } d && ShowProgress ? StepsOf(d) : [];
        if (!steps.SequenceEqual(ProgressSteps)) Sync(ProgressSteps, steps, (a, b) => a == b);
        Notify(nameof(ShowProgress), nameof(ProgressFailed), nameof(ProgressRunning), nameof(ProgressTitle), nameof(ProgressElapsed), nameof(ProgressHint),
            nameof(HasProgressHint), nameof(ShowStatus), nameof(ShowTimeline), nameof(ShowNewIntro));
    }

    /// <summary>
    /// The steps of <paramref name="d"/> so far, each with how long it took (or has taken while it runs). A step's time
    /// is from when the phone first saw it to when it saw the next one, so it is noted for every delivery, on screen or not.
    /// </summary>
    private List<ProgressStep> StepsOf(Delivery d)
    {
        var p = d.Sent;
        var steps = new List<(string Text, ProgressState State)>();
        if (p.Kind == WorkspaceCommandKind.Reopen && d.Title is { } title) steps.Add((title, p.Done ? p.Failed ? ProgressState.Failed : ProgressState.Done : ProgressState.Active));
        var result = _results.Get(p.CommandId);
        var pushed = result is not null || _sync is null || _sync.Status.LastSyncedAt is { } synced && synced >= p.At;
        steps.Add((pushed ? "Sent from this phone" : "Sending from this phone…", pushed ? ProgressState.Done : p.Failed ? ProgressState.Failed : ProgressState.Active));
        if (pushed)
        {
            steps.Add(result is not null
                ? ($"{HostName} picked it up", ProgressState.Done)
                : (p.Failed ? $"{HostName} did not answer" : $"Waiting for {HostName}…", p.Failed ? ProgressState.Failed : ProgressState.Active));
            for (var i = 0; i < d.PcSteps.Count; i++)
            {
                var last = i == d.PcSteps.Count - 1;
                steps.Add((d.PcSteps[i], last && !p.Done ? ProgressState.Active : last && p.Failed && result?.Status == WorkspaceCommandStatus.Failed ? ProgressState.Failed : ProgressState.Done));
            }
            switch (result?.Status)
            {
                case WorkspaceCommandStatus.Received:
                    steps.Add((p.Kind == WorkspaceCommandKind.Reopen ? "Open in VS Code" : "Claude received it", ProgressState.Done));
                    if (d.AwaitGaveUp) steps.Add(("It did not show up here yet. Pull the tabs again with ⟳, or look on the PC.", ProgressState.Failed));
                    else if (AwaitingTab(d)) steps.Add(("Opening it here…", ProgressState.Active));
                    break;
                case WorkspaceCommandStatus.Sent:
                    steps.Add((result.Error ?? "Typed into VS Code; Claude has not confirmed it yet", ProgressState.Done));
                    break;
                case WorkspaceCommandStatus.Failed:
                    steps.Add((result.Error ?? "It could not be sent.", ProgressState.Failed));
                    break;
            }
        }
        var now = _time.GetLocalNow();
        // When each step was first seen (by its place in the list: a step's words change as it ends, its place does not).
        for (var i = d.StepAt.Count; i < steps.Count; i++) d.StepAt.Add(now);
        var list = new List<ProgressStep>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var start = i == 0 ? p.At : d.StepAt[i];
            var (text, state) = steps[i];
            var took = i + 1 < steps.Count ? Took(d.StepAt[i + 1] - start)
                : state == ProgressState.Active ? Took(now - start)
                // The last word: how long it all took.
                : steps.Count > 1 ? "in " + Took(d.StepAt[i] - p.At) : "";
            list.Add(new ProgressStep(text, state, took));
        }
        return list;
    }

    /// <summary>"0.6 s", "12 s", "1 min 05 s".</summary>
    private static string Took(TimeSpan t) => t.TotalSeconds < 10 ? $"{Math.Max(0, t.TotalSeconds):0.0} s" : Seconds(t);

    /// <summary>Said under the running step of <paramref name="d"/> when it takes longer than it should.</summary>
    private string HintOf(Delivery d)
    {
        if (d.Sent is not { Done: false } p) return "";
        var waited = _time.GetLocalNow() - p.At;
        if (_results.Get(p.CommandId) is null)
            return waited > TimeSpan.FromSeconds(12) ? $"{HostName} has not picked it up yet. Is the PC awake, with Helm running? It keeps trying." : "";
        return waited > TimeSpan.FromSeconds(25) ? "Slower than usual: VS Code or Claude may be busy on the PC. It keeps going." : "";
    }

    /// <summary>The steps of <paramref name="d"/> as timeline rows; the running one says what holds it up when it is slow.</summary>
    private IEnumerable<TimelineRow> StepRows(Delivery d)
    {
        var steps = StepsOf(d);
        var hint = HintOf(d);
        return steps.Select((step, i) => TimelineRow.Step(step, i == steps.Count - 1 && step.IsActive ? hint : null));
    }

    /// <summary>The conversation area shows: a session is open, or a new session or reopened one is on its way.</summary>
    public bool ShowTimeline => IsSessionOpen || Card is not null && ShowProgress;

    /// <summary>The new-session page's words, until its first prompt is sent (then its steps show instead).</summary>
    public bool ShowNewIntro => IsNewSession && !ShowTimeline;

    private static string Seconds(TimeSpan t) => t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)} s" : $"{(int)t.TotalMinutes} min {t.Seconds:00} s";

    // ---- How fresh is what you see

    /// <summary>Live: changes arrive within seconds and the PC was heard from lately. Delayed: polling, or the PC quiet a while.</summary>
    public bool FreshLive => HostOnline && _sync?.IsLive != false && PcQuiet < TimeSpan.FromSeconds(75);
    public bool FreshDelayed => HostOnline && !FreshLive;
    public bool FreshOffline => !HostOnline;
    public bool IsSyncing => _sync?.Status.State == SyncState.Syncing;

    /// <summary>The chip: "Live", "40 s ago", "Offline".</summary>
    public string FreshnessText => FreshOffline ? "Offline" : FreshLive ? "Live" : Ago(PcQuiet);

    /// <summary>How long since the PC last wrote (its heartbeat, every 30 seconds while on).</summary>
    private TimeSpan PcQuiet => Host is { } h ? _time.GetLocalNow() - (_pcAnsweredAt is { } a && a > h.LastSeen ? a : h.LastSeen) : TimeSpan.MaxValue;

    public string StatusSync => _sync is null ? "Helm Sync is not set up on this phone."
        : (_sync.IsLive ? "Live connection to Helm Sync: changes from the PC arrive within a second or two." : "No live connection to Helm Sync right now: the phone checks every 30 seconds.")
          + (_sync.Status.LastSyncedAt is { } at ? $" Last synced {Ago(_time.GetLocalNow() - at)} ({at.ToLocalTime():HH:mm:ss})." : "")
          + (_sync.Status.LastError is { Length: > 0 } e && _sync.Status.State is SyncState.Offline or SyncState.Error ? $" Problem: {e}" : "");

    public string StatusPc => Host is not { } h ? "No PC chosen."
        : !HostOnline ? $"{h.MachineName} is offline (last heard {h.LastSeen.ToLocalTime():dd/MM HH:mm}). Turn on Workspace Controller in Helm there."
        : $"{h.MachineName} last wrote {Ago(_time.GetLocalNow() - h.LastSeen)}; it writes at least every 30 seconds while on."
          + (_pcAnsweredAt is { } a ? $" It answered your check {Ago(_time.GetLocalNow() - a)}" + (_pcRoundTrip is { } rt ? $", in {rt.TotalSeconds:0.0} s." : ".") : "");

    public string StatusSession => Session is { } s
        ? $"This session last changed at {s.UpdatedAt.ToLocalTime():HH:mm:ss} ({Describe(s)})." + (s.Items.LastOrDefault()?.At is { } step ? $" Its latest step is from {step.ToLocalTime():HH:mm:ss}." : "")
        : "No session open.";

    public string StatusCommand
    {
        get
        {
            var elsewhere = _deliveries.Count(d => !d.Sent.Done && !Shows(d));
            var others = elsewhere == 0 ? "" : elsewhere == 1 ? " 1 more is on its way in another tab." : $" {elsewhere} more are on their way in other tabs.";
            return (Latest is { Sent.Done: false } d ? $"Your {Describe(d.Sent.Kind)} is on its way ({Seconds(_time.GetLocalNow() - d.Sent.At)})." + (d.PcSteps.LastOrDefault() is { } last ? $" The PC: {last}." : "")
                : _pingId is not null ? "Checking the PC…" : elsewhere > 0 ? "Nothing of yours is on its way here." : "Nothing of yours is on its way.") + others;
        }
    }

    public bool HasPcActivities => PcActivities.Count > 0;

    private static string Describe(WorkspaceCommandKind kind) => kind switch
    {
        WorkspaceCommandKind.NewSession => "new session",
        WorkspaceCommandKind.Reopen => "session from History",
        WorkspaceCommandKind.Answer => "answer",
        WorkspaceCommandKind.Prompt => "message",
        _ => "change",
    };

    private static string Ago(TimeSpan t) =>
        t == TimeSpan.MaxValue ? "never" : t < TimeSpan.FromSeconds(5) ? "just now" : t.TotalSeconds < 60 ? $"{(int)t.TotalSeconds} s ago"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min ago" : $"{(int)t.TotalHours} h ago";

    /// <summary>Checks now: syncs at once and asks the PC to answer (so "last heard" is fresh).</summary>
    [RelayCommand]
    private void RefreshNow()
    {
        _sync?.RequestSyncSoon();
        if (_machineId is null || !HostOnline || _pingId is not null) return;
        var ping = new WorkspaceCommand { MachineId = _machineId, Kind = WorkspaceCommandKind.Ping, CreatedAt = _time.GetLocalNow() };
        _pingId = ping.Id;
        _pingAt = ping.CreatedAt;
        _commands.Upsert(ping.Id, ping);
        NotifyFreshness();
    }

    private void NotifyFreshness()
    {
        var activities = Host?.Activities ?? [];
        if (!activities.SequenceEqual(PcActivities)) Sync(PcActivities, activities, (a, b) => a == b);
        Notify(nameof(FreshLive), nameof(FreshDelayed), nameof(FreshOffline), nameof(FreshnessText), nameof(IsSyncing), nameof(StatusSync),
            nameof(StatusPc), nameof(StatusSession), nameof(StatusCommand), nameof(HasPcActivities));
    }

    /// <summary>Once a second: the progress card's clock, the "Working…" line, and how fresh the screen is.</summary>
    private void Tick()
    {
        foreach (var d in _deliveries.Where(d => AwaitingTab(d) && d.AnsweredAt is { } answered && _time.GetLocalNow() - answered > AwaitTabFor))
        {
            d.AwaitGaveUp = true;
            d.AwaitSession = null;
        }
        if (_pingId is not null && _time.GetLocalNow() - _pingAt > AnswerTimeout) _pingId = null;
        if (ShowProgress || ProgressSteps.Count > 0) RebuildProgress();
        var stepping = _deliveries.Any(d => Shows(d) && (!d.Sent.Done || AwaitingTab(d)));
        if ((IsWorking || stepping) && !_deferFill && _timelineFor == (IsNewSession ? null : Session?.SessionId)) FillTimelineCore();
        NotifyFreshness();
        Notify(nameof(AgeText));
    }

    /// <summary>
    /// The screen is in front (set by the page): only then do the clocks tick, so a phone with Helm in the background
    /// is not woken every second.
    /// </summary>
    public bool IsOnScreen
    {
        get => _onScreen;
        set
        {
            if (_onScreen == value) return;
            _onScreen = value;
            var every = value ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan;
            _ticker.Change(every, every);
            if (value) Tick();
        }
    }

    private bool _onScreen;

    /// <summary>A new or reopened tab should show up within this long after the PC opened it.</summary>
    public static readonly TimeSpan AwaitTabFor = TimeSpan.FromSeconds(20);

    // ---- Refresh

    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var now = _time.GetLocalNow();
            var hosts = _hosts.All().Select(i => i.Value).OrderByDescending(h => h.IsOnline(now)).ThenBy(h => h.MachineName, StringComparer.CurrentCultureIgnoreCase).ToList();
            Sync(Hosts, hosts.Select(h => new HostChoice(h, h.IsOnline(now))).ToList(), (a, b) => a.Host == b.Host && a.IsOnline == b.IsOnline);
            if (SelectedHost is null) _machineId = Hosts.FirstOrDefault()?.MachineId;

            var records = _machineId is null ? [] : _sessions.All().Select(i => i.Value).Where(r => r.MachineId == _machineId).ToList();
            // The open conversation's new answer is seen before anything is counted.
            if (!IsNewSession && _sessionId is not null && records.FirstOrDefault(r => r.SessionId == _sessionId) is { } open) MarkSeen(open);

            IReadOnlyList<HostWindowInfo> windows = Host?.Windows ?? [];
            // Sessions outside VS Code get a tab of their own, after the windows.
            if (records.Any(r => r.IsOutside)) windows = [.. windows, ProjectTab.OutsideWindow];
            if (!windows.Any(w => SameFolder(w.Folder, _folder)))
            {
                // A notification may name a session of a window by its folder; else the first window.
                _folder = windows.FirstOrDefault(w => records.Any(r => r.SessionId == _sessionId && SameFolder(r.WindowFolder, w.Folder)))?.Folder ?? windows.FirstOrDefault()?.Folder;
            }
            var window = windows.FirstOrDefault(w => SameFolder(w.Folder, _folder));
            var inWindow = window is null ? [] : records.Where(r => SameFolder(r.WindowFolder, window.Folder)).ToList();
            // A tab closed from the phone stays hidden until the PC stops listing it (then it is forgotten).
            _closing.RemoveWhere(id => !records.Any(r => r.SessionId == id && (window is null || window.OpenSessions.Count == 0 || window.OpenSessions.Contains(id))));
            var inTabs = window is null ? [] : Ordered(inWindow.Where(r => !_closing.Contains(r.SessionId)).ToList(), window);
            foreach (var d in _deliveries.Where(d => d.AwaitSession is not null).ToList())
            {
                // The new or reopened tab is here: it opens if you still look at its project, else it simply waits there.
                if (SameFolder(d.Folder, _folder) && inTabs.Any(r => r.SessionId == d.AwaitSession) && (d.Sent.Kind == WorkspaceCommandKind.Reopen || IsNewSession))
                {
                    _sessionId = d.AwaitSession;
                    d.AwaitSession = null;
                    IsNewSession = false;
                }
                else if (records.Any(r => r.SessionId == d.AwaitSession && SameFolder(r.WindowFolder, d.Folder)) && !Shows(d)) _deliveries.Remove(d);
            }
            if (!IsNewSession && !inTabs.Any(r => r.SessionId == _sessionId))
            {
                // The tab went away (closed) or none was picked: the one that needs you, the one in front, the first.
                var pick = inTabs.FirstOrDefault(r => r.State is SessionState.NeedsPermission or SessionState.Question)
                    ?? inTabs.FirstOrDefault(r => r.SessionId == window?.ActiveSession) ?? inTabs.FirstOrDefault();
                _sessionId = pick?.SessionId;
                if (pick is null && window is not null && window.Folder.Length > 0 && !_deliveries.Any(d => d.AwaitSession is not null && SameFolder(d.Folder, window.Folder))) IsNewSession = true;
                if (pick is not null) MarkSeen(pick);
            }
            Sync(SessionTabs, inTabs.Select(r => new SessionTab(r, IsUnseen(r), !IsNewSession && r.SessionId == _sessionId)).ToList(),
                (a, b) => ReferenceEquals(a.Record, b.Record) && a.Unseen == b.Unseen && a.IsSelected == b.IsSelected);

            Sync(Projects, windows.Select(w =>
            {
                var inWindow = records.Where(r => SameFolder(r.WindowFolder, w.Folder)).ToList();
                return new ProjectTab(w, inWindow.Count(r => r.State is SessionState.NeedsPermission or SessionState.Question || IsUnseen(r) && r.SessionId != _sessionId),
                    inWindow.Any(r => r.State == SessionState.Working), SameFolder(w.Folder, _folder));
            }).ToList(), (a, b) => a.Window == b.Window && a.Attention == b.Attention && a.IsWorking == b.IsWorking && a.IsSelected == b.IsSelected);

            if (!HasReview) ShowReview = false;
            if (!HasPlan) PlanFeedback = false;
            ExpireOverrides();
        }
        finally
        {
            _refreshing = false;
        }
        FollowView();
        FillTimeline();
        ShowPending();
        if (Sheet is WorkspaceSheet.Model or WorkspaceSheet.Mode) FillChoices();
        if (Sheet == WorkspaceSheet.Agents) FillAgents();
        if (Sheet == WorkspaceSheet.History) FillHistory();
        Notify(nameof(SelectedHost), nameof(HasHosts), nameof(HostOnline), nameof(HostName), nameof(HostStatus), nameof(HasProjects), nameof(SelectedProject),
            nameof(AutoModeAvailable), nameof(IsOutsideProject), nameof(CanStartNew));
        NotifySession();
        RebuildProgress();
        NotifyFreshness();
    }

    /// <summary>The window's sessions in the order of its Claude tabs; others (in the side bar, at work) after them.</summary>
    private static List<WorkspaceSessionRecord> Ordered(List<WorkspaceSessionRecord> records, HostWindowInfo window)
    {
        if (window.Folder.Length == 0) return records.OrderByDescending(r => r.UpdatedAt).ToList();
        var order = window.OpenSessions.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
        return records.OrderBy(r => order.TryGetValue(r.SessionId, out var i) ? i : int.MaxValue).ThenBy(r => r.StartedAt).ToList();
    }

    private void NotifySession()
    {
        Notify(nameof(SelectedTab), nameof(Session), nameof(IsSessionOpen), nameof(OpenSessionId), nameof(SessionTitle), nameof(SessionStateText),
            nameof(IsWorking), nameof(NeedsYou), nameof(HasReview), nameof(ReviewLabel), nameof(CanType), nameof(PromptHint), nameof(HistoryTitle),
            nameof(IsOutsideSession), nameof(CanStop), nameof(CanAttach), nameof(ImagesSupported));
        NotifyChips();
        SendCommand.NotifyCanExecuteChanged();
    }

    private void NotifyChips() =>
        Notify(nameof(ModelLabel), nameof(EffortLabel), nameof(HasEffort), nameof(IsUltracode), nameof(ModeName), nameof(ModeKey), nameof(ModeIsManual),
            nameof(ModeIsEdits), nameof(ModeIsPlan), nameof(ModeIsAuto), nameof(ContextFraction), nameof(ContextText), nameof(AgeText), nameof(AgentCount),
            nameof(AgentsText), nameof(HasAgents), nameof(EffortHeading));

    private void Notify(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
    }

    /// <summary>The pending card follows the open session (new questions only rebuild when the request changed).</summary>
    private void ShowPending()
    {
        var p = Pending;
        if (p?.RequestId != _shownRequest)
        {
            _shownRequest = p?.RequestId;
            Questions.Clear();
            foreach (var q in p?.Questions ?? []) Questions.Add(new QuestionChoice(q));
            PlanBlocks = p?.Plan is { } plan ? MarkdownBlocks.Parse(plan) : [];
            _denyNext = false;
        }
        Notify(nameof(Pending), nameof(HasPermission), nameof(HasQuestion), nameof(HasPlan), nameof(PendingTitle), nameof(PendingDetail),
            nameof(PendingExpires), nameof(PlanBlocks), nameof(HasReview), nameof(ReviewLabel));
        AllowCommand.NotifyCanExecuteChanged();
        DenyCommand.NotifyCanExecuteChanged();
        TellInsteadCommand.NotifyCanExecuteChanged();
        SendAnswersCommand.NotifyCanExecuteChanged();
        ApprovePlanCommand.NotifyCanExecuteChanged();
        KeepPlanningCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The timeline of the open session, changed in place (a row is replaced only when its step changed), so a live
    /// update neither flickers nor moves what you are reading. Your prompt on its way shows at the end until Claude has
    /// it; "Working…" shows while Claude is between steps.
    /// </summary>
    private void FillTimeline()
    {
        SwitchTimeline();
        if (_deferFill && DeferTimeline is { } defer)
        {
            _deferFill = false;
            var forSession = _timelineFor;
            defer(() =>
            {
                if (_timelineFor == forSession) FillTimelineCore();
            });
            return;
        }
        _deferFill = false;
        FillTimelineCore();
    }

    /// <summary>
    /// Another conversation opens with its own list: the one kept from before (already drawn), or a new one. Lists of
    /// sessions that are no longer tabs are let go.
    /// </summary>
    private void SwitchTimeline()
    {
        var openId = IsNewSession ? null : Session?.SessionId;
        if (openId == _timelineFor && _timelines.ContainsKey(openId ?? "")) return;
        _timelineFor = openId;
        _timelineLimit = TimelinePage;
        var key = openId ?? "";
        if (!_timelines.TryGetValue(key, out var list)) _timelines[key] = list = [];
        foreach (var gone in _timelines.Keys.Where(k => k.Length > 0 && k != key && !SessionTabs.Any(t => t.SessionId == k)).ToList()) _timelines.Remove(gone);
        Timeline = list;
        Notify(nameof(Timeline), nameof(CachedTimelines));
    }

    private void FillTimelineCore()
    {
        var rows = new List<TimelineRow>();
        if (Session is { } s)
        {
            rows.AddRange(s.Items.Select(i => new TimelineRow(WithLocalImages(i))));
            if (rows.Count == 0 && (s.LastPrompt is not null || s.LastReply is not null))
            {
                // A session of an older PC (or one not read yet): its last prompt and answer.
                if (s.LastPrompt is { } p) rows.Add(new TimelineRow(new TranscriptItem { Kind = TranscriptItemKind.User, Text = p }));
                if (s.LastReply is { } r) rows.Add(new TimelineRow(new TranscriptItem { Kind = TranscriptItemKind.Answer, Text = r, At = s.UpdatedAt }));
            }
            // The Stop hook brings the last answer a moment before the transcript has it.
            if (s.State == SessionState.Done && s.LastReply is { Length: > 0 } last && rows.Count > 0 && !rows[^1].IsAnswer && !rows[^1].Running
                && !rows.Any(r => r.IsAnswer && r.Text.Trim() == last.Trim()))
                rows.Add(new TimelineRow(new TranscriptItem { Kind = TranscriptItemKind.Answer, Text = last, At = s.UpdatedAt }));
            var mine = _deliveries.LastOrDefault(d => d.Sent.Text is not null && d.Sent.SessionId == s.SessionId);
            if (mine is { Sent: { Text: { } text } pending } && !Arrived(s, text, pending.Images, pending.At))
            {
                // Your message, then each step of its way on the rail.
                rows.Add(TimelineRow.Sending(text, "", mine.Failed, pending.At, pending.Images));
                rows.AddRange(StepRows(mine));
            }
            else
            {
                // Claude has it: one line under your message says how long that took.
                if (mine is { Sent: { Text: { } said, Done: true, Failed: false } }
                    && rows.FindLastIndex(r => r.IsUser && SessionRegistry.Normalize(r.Text) == SessionRegistry.Normalize(said)) is var at and >= 0
                    && StepsOf(mine) is { Count: > 0 } done)
                    rows.Insert(at + 1, TimelineRow.Step(done[^1]));
                if (s.State == SessionState.Working && (rows.Count == 0 || !rows[^1].Running))
                    rows.Add(TimelineRow.Working(WorkingText(s)));
            }
            if (Card is { Sent.Kind: WorkspaceCommandKind.Reopen } reopen && ShowProgress) rows.AddRange(StepRows(reopen));
        }
        else if (IsNewSession && Card is { Sent: { Kind: WorkspaceCommandKind.NewSession, Text: { } first } sent } started)
        {
            rows.Add(TimelineRow.Sending(first, "", started.Failed, sent.At, sent.Images));
            rows.AddRange(StepRows(started));
        }
        else if (Card is { Sent.Kind: WorkspaceCommandKind.Reopen } opening && ShowProgress)
        {
            rows.AddRange(StepRows(opening));
        }
        // Only the last steps are drawn; "Show earlier" draws more.
        HiddenSteps = Math.Max(0, rows.Count - _timelineLimit);
        if (HiddenSteps > 0) rows = rows.GetRange(HiddenSteps, rows.Count - HiddenSteps);
        Merge(rows);
        Notify(nameof(ShowStatus), nameof(HiddenSteps), nameof(HasHiddenSteps), nameof(HiddenStepsText));
    }

    /// <summary>
    /// Puts <paramref name="rows"/> into the timeline touching only what changed. The record keeps a sliding window
    /// of steps, so old ones fall off the front while new ones come at the end: rows are matched by their step, not by
    /// their place, and the ones in between stay as they are (no redraw).
    /// </summary>
    private void Merge(List<TimelineRow> rows)
    {
        // Steps gone from the front.
        while (Timeline.Count > 0 && rows.FindIndex(r => r.Same(Timeline[0])) is var at && at != 0)
        {
            if (at < 0) Timeline.RemoveAt(0);
            else
            {
                // Earlier steps shown again ("Show earlier"): inserted before.
                for (var i = 0; i < at; i++) Timeline.Insert(i, rows[i]);
                break;
            }
        }
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < Timeline.Count)
            {
                if (!Timeline[i].Same(rows[i])) Timeline[i] = rows[i];
            }
            else Timeline.Add(rows[i]);
        }
        while (Timeline.Count > rows.Count) Timeline.RemoveAt(Timeline.Count - 1);
    }

    /// <summary>"Working… 2m 05s · last step 40 s ago": Claude is at it, and you can tell it has not stalled.</summary>
    private string WorkingText(WorkspaceSessionRecord s)
    {
        var now = _time.GetLocalNow();
        var text = "Working…";
        if (s.WorkingSince is { } since) text += " " + AgentRow.Format(now - since);
        if (s.Agents.Count > 0) text += $" · {AgentsText} at work";
        var last = s.Items.LastOrDefault()?.At ?? s.UpdatedAt;
        if (now - last > TimeSpan.FromSeconds(30)) text += $" · last step {Ago(now - last)}";
        return text;
    }

    /// <summary>The prompt shows in the transcript (it arrived, and Claude has it).</summary>
    private static bool Arrived(WorkspaceSessionRecord s, string text, IReadOnlyList<WorkspaceImage> images, DateTimeOffset since) =>
        s.Items.Any(i => i.Kind == TranscriptItemKind.User && SessionRegistry.Normalize(i.Text) == SessionRegistry.Normalize(text) && (i.At is null || i.At >= since.AddMinutes(-2))
            && (images.Count == 0 || SameImages(i.Images, images)));

    private void FillChoices()
    {
        var current = CurrentModel;
        ModelOptions.Clear();
        foreach (var m in ModelList) ModelOptions.Add(new ModelOption(m, current is not null && m.Value == current.Value));
        ModeOptions.Clear();
        foreach (var mode in PermissionModes.Cycle(Host?.AutoModeAvailable ?? false)) ModeOptions.Add(new ModeOption(mode, mode == CurrentMode));
        if (CurrentMode is not (PermissionModes.Manual or PermissionModes.EditAutomatically or PermissionModes.Plan or PermissionModes.Auto))
            ModeOptions.Add(new ModeOption(CurrentMode, true));
        EffortDots.Clear();
        var levels = current?.EffortLevels is { Count: > 0 } l ? l : PermissionModes.EffortLevels;
        foreach (var level in PermissionModes.EffortLevels) EffortDots.Add(new EffortDot(level, level == CurrentEffort, levels.Contains(level)));
        Notify(nameof(EffortHeading));
    }

    private void FillAgents()
    {
        AgentRows.Clear();
        var now = _time.GetLocalNow();
        foreach (var a in Session?.Agents ?? []) AgentRows.Add(new AgentRow(a, now));
    }

    /// <summary>The History sheet: the sessions open in VS Code, then the earlier ones by day; filtered by the search.</summary>
    private void FillHistory()
    {
        HistoryRows.Clear();
        if (SelectedProject is not { } project || _machineId is null) return;
        var q = HistoryQuery.Trim();
        bool Hit(string title, string more) => q.Length == 0 || title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || more.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        var open = SessionTabs.Where(t => Hit(t.Title, t.Record.LastPrompt ?? "")).ToList();
        if (open.Count > 0) HistoryRows.Add(new HistoryRow { Day = "Open in VS Code" });
        foreach (var t in open)
            HistoryRows.Add(new HistoryRow
            {
                SessionId = t.SessionId,
                Title = t.Title,
                Detail = Describe(t.Record) + (t.Record.Model is { } m ? " · " + ModelName(m) : ""),
                IsOpen = true,
                NeedsYou = t.NeedsYou,
                IsWorking = t.IsWorking,
            });
        var openIds = SessionTabs.Select(t => t.SessionId).ToHashSet(StringComparer.Ordinal);
        var past = _history?.Get(WorkspaceSync.HistoryId(_machineId, project.Folder))?.Sessions ?? [];
        string? day = null;
        var today = _time.GetLocalNow().Date;
        foreach (var h in past.Where(h => !openIds.Contains(h.SessionId) && Hit(h.Title, h.FirstPrompt)))
        {
            var local = h.LastAt.ToLocalTime();
            var label = local.Date == today ? "Today" : local.Date == today.AddDays(-1) ? "Yesterday" : local.ToString("MMM d");
            if (label != day) HistoryRows.Add(new HistoryRow { Day = day = label });
            HistoryRows.Add(new HistoryRow
            {
                SessionId = h.SessionId,
                Title = h.Title,
                Detail = $"{h.Messages} message{(h.Messages == 1 ? "" : "s")}" + (h.Model is { } m ? " · " + ModelName(m) : "") + (h.LastReply.Length > 0 ? " · " + h.LastReply : ""),
                When = local.ToString("HH:mm"),
            });
        }
        if (HistoryRows.Count == 0) HistoryRows.Add(new HistoryRow { Day = q.Length > 0 ? "Nothing matches" : "No earlier sessions" });
    }

    private string ModelName(string id) => ModelList.Where(m => m.ResolvedModel == id).OrderBy(m => m.Value == DefaultModel).FirstOrDefault()?.DisplayName ?? id.Replace("claude-", "");

    // ---- Changes you made that the transcript does not show yet

    private sealed record Override(string? Model = null, string? ModelBase = null, string? Effort = null, string? EffortBase = null, string? Mode = null, string? ModeBase = null);

    private void SetOverride(WorkspaceSessionRecord s, Func<Override, Override> change)
    {
        _overrides[s.SessionId] = change(_overrides.GetValueOrDefault(s.SessionId) ?? new Override());
        NotifyChips();
    }

    /// <summary>Your choice shows until the session reports something else than what it reported when you chose.</summary>
    private string? OverrideOf(WorkspaceSessionRecord s, Func<Override, string?> pick) => _overrides.TryGetValue(s.SessionId, out var o) ? pick(o) : null;

    private void ExpireOverrides()
    {
        foreach (var (id, o) in _overrides.ToList())
        {
            if (_sessions.Get(id) is not { } s) { _overrides.Remove(id); continue; }
            var next = o with
            {
                Model = o.Model is not null && s.Model != o.ModelBase ? null : o.Model,
                Effort = o.Effort is not null && s.Effort != o.EffortBase ? null : o.Effort,
                Mode = o.Mode is not null && s.PermissionMode != o.ModeBase ? null : o.Mode,
            };
            if (next.Model is null && next.Effort is null && next.Mode is null) _overrides.Remove(id);
            else _overrides[id] = next;
        }
    }

    private void RevertOverride(SentCommand pending)
    {
        if (pending.SessionId is not { } id || !_overrides.TryGetValue(id, out var o)) return;
        _overrides[id] = pending.Kind switch
        {
            WorkspaceCommandKind.SetModel => o with { Model = null },
            WorkspaceCommandKind.SetEffort => o with { Effort = null },
            WorkspaceCommandKind.SetMode or WorkspaceCommandKind.Answer => o with { Mode = null },
            _ => o,
        };
        if (pending.Kind == WorkspaceCommandKind.SetEffort && _ultracode.ContainsKey(id)) _ultracode[id] = !_ultracode[id];
        NotifyChips();
    }

    private sealed record SentCommand(string CommandId, WorkspaceCommandKind Kind, string? SessionId, string? Text, DateTimeOffset At, bool Done = false, bool Failed = false)
    {
        public IReadOnlyList<WorkspaceImage> Images { get; init; } = [];
    }

    /// <summary>One command on its way and what its card shows: the steps the PC reported, its status line, when it answered.</summary>
    private sealed class Delivery(SentCommand sent, string? folder, string? title, string? awaitSession)
    {
        public SentCommand Sent { get; set; } = sent;

        /// <summary>The project (VS Code window folder) it was sent from.</summary>
        public string? Folder { get; } = folder;

        public string? Title { get; } = title;

        /// <summary>The new or reopened session whose tab should open here.</summary>
        public string? AwaitSession { get; set; } = awaitSession;

        public bool AwaitGaveUp { get; set; }
        public List<string> PcSteps { get; } = [];

        /// <summary>When the phone first saw each step (by its place in the list).</summary>
        public List<DateTimeOffset> StepAt { get; } = [];

        public DateTimeOffset? AnsweredAt { get; set; }
        public string? Status { get; set; }
        public bool Failed { get; set; }

        /// <summary>You saw how it ended: it is put away when you leave its place.</summary>
        public bool SeenDone { get; set; }
    }

    // ---- Seen

    private bool IsUnseen(WorkspaceSessionRecord r) =>
        r.State == SessionState.Done && (!_settings.Current.Seen.TryGetValue(r.SessionId, out var seen) || seen < r.UpdatedAt);

    /// <summary>True when it was not seen before.</summary>
    private bool MarkSeen(WorkspaceSessionRecord r)
    {
        if (_settings.Current.Seen.TryGetValue(r.SessionId, out var seen) && seen >= r.UpdatedAt) return false;
        _settings.Update(s =>
        {
            s.Seen[r.SessionId] = r.UpdatedAt;
            // Only sessions still listed somewhere are worth remembering.
            var live = _sessions.All().Select(i => i.Id).ToHashSet();
            foreach (var gone in s.Seen.Keys.Where(k => !live.Contains(k)).ToList()) s.Seen.Remove(gone);
        });
        return true;
    }

    // ---- Helpers

    /// <summary>Puts <paramref name="items"/> into <paramref name="target"/>, touching only what changed (lists do not jump).</summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items, Func<T, T, bool> same)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count)
            {
                if (!same(target[i], items[i])) target[i] = items[i];
            }
            else target.Add(items[i]);
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }

    private static bool SameFolder(string? a, string? b) =>
        a is not null && b is not null && SessionRegistry.FolderKey(a) == SessionRegistry.FolderKey(b);

    private static string Tokens(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000.0:0}k" : n.ToString();

    private static string Short(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h" : $"{Math.Max(0, (int)t.TotalMinutes)}m";
}
