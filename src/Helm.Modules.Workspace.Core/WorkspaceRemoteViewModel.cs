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
}

/// <summary>The bottom sheets of the control screen.</summary>
public enum WorkspaceSheet { None, Pc, Model, Mode, Agents, History, Context }

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
    private string? _awaitSession;
    private SentCommand? _pending;
    private bool _denyNext;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _prompt = "";

    [ObservableProperty] private bool _runInBackground;
    [ObservableProperty] private string? _sendStatus;
    [ObservableProperty] private bool _sendFailed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

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
        ISyncedCollection<WorkspaceHistoryRecord>? history = null)
    {
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
        _runInBackground = _settings.Current.RunInBackground;
        _machineId = _settings.Current.LastMachineId is { Length: > 0 } last ? last : null;
        Refresh();
    }

    public ObservableCollection<HostChoice> Hosts { get; } = [];
    public ObservableCollection<ProjectTab> Projects { get; } = [];
    public ObservableCollection<SessionTab> SessionTabs { get; } = [];

    /// <summary>The open conversation, oldest first (the view keeps the end in sight, as VS Code does).</summary>
    public ObservableCollection<TimelineRow> Timeline { get; } = [];

    public ObservableCollection<SkillInfo> Suggestions { get; } = [];

    /// <summary>The questions of a held AskUserQuestion of the open session.</summary>
    public ObservableCollection<QuestionChoice> Questions { get; } = [];

    public ObservableCollection<ModelOption> ModelOptions { get; } = [];
    public ObservableCollection<ModeOption> ModeOptions { get; } = [];
    public ObservableCollection<EffortDot> EffortDots { get; } = [];
    public ObservableCollection<AgentRow> AgentRows { get; } = [];
    public ObservableCollection<HistoryRow> HistoryRows { get; } = [];

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
    public string SessionStateText => Session is { } r ? Describe(r) : IsNewSession && SelectedProject is { } p ? $"Opens a Claude tab in {p.Name}" : "";
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
    public bool ShowStatus => SendStatus is { Length: > 0 } && Timeline.LastOrDefault()?.HasDelivery != true;

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
        : "Ask Claude to edit…";

    // ---- Sheets

    public bool IsSheetOpen => Sheet != WorkspaceSheet.None;
    public bool IsPcSheet => Sheet == WorkspaceSheet.Pc;
    public bool IsModelSheet => Sheet == WorkspaceSheet.Model;
    public bool IsModeSheet => Sheet == WorkspaceSheet.Mode;
    public bool IsAgentsSheet => Sheet == WorkspaceSheet.Agents;
    public bool IsHistorySheet => Sheet == WorkspaceSheet.History;
    public bool IsContextSheet => Sheet == WorkspaceSheet.Context;
    public bool ShowsEffort => IsModelSheet || IsModeSheet;
    public string HistoryTitle => SelectedProject is { } p ? $"History · {p.Name}" : "History";
    public string EffortHeading => $"Effort ({(PermissionModes.EffortLabel(CurrentEffort) is { Length: > 0 } e ? e : "default")})";

    partial void OnSheetChanged(WorkspaceSheet value)
    {
        if (value == WorkspaceSheet.History) FillHistory();
        if (value is WorkspaceSheet.Model or WorkspaceSheet.Mode) FillChoices();
        if (value == WorkspaceSheet.Agents) FillAgents();
        Notify(nameof(IsSheetOpen), nameof(IsPcSheet), nameof(IsModelSheet), nameof(IsModeSheet), nameof(IsAgentsSheet), nameof(IsHistorySheet),
            nameof(IsContextSheet), nameof(ShowsEffort));
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
        _folder = project.Folder;
        _sessionId = null;
        IsNewSession = false;
        ShowReview = false;
        ClearStatus();
        Refresh();
    }

    [RelayCommand]
    private void SelectSession(SessionTab? tab)
    {
        if (tab is null) return;
        _sessionId = tab.SessionId;
        IsNewSession = false;
        ShowReview = false;
        PlanFeedback = false;
        _denyNext = false;
        ClearStatus();
        Refresh();
    }

    [RelayCommand]
    private void NewSession()
    {
        if (SelectedProject is null) return;
        IsNewSession = true;
        ShowReview = false;
        PlanFeedback = false;
        Sheet = WorkspaceSheet.None;
        ClearStatus();
        Refresh();
    }

    [RelayCommand]
    private void CloseTab(SessionTab? tab)
    {
        if (tab is null || !HostOnline) return;
        Write(new WorkspaceCommand { MachineId = _machineId!, Kind = WorkspaceCommandKind.CloseTab, SessionId = tab.SessionId, CreatedAt = _time.GetLocalNow() },
            "Closing the tab in VS Code…");
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
        if (!HostOnline || SelectedProject is not { } project) return;
        _awaitSession = row.SessionId;
        Write(new WorkspaceCommand { MachineId = _machineId!, Kind = WorkspaceCommandKind.Reopen, SessionId = row.SessionId, Folder = project.Folder, CreatedAt = _time.GetLocalNow() },
            "Opening it in VS Code…");
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

    private bool CanSend() => !IsSending && CanType && !string.IsNullOrWhiteSpace(Prompt);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = Prompt.Trim();
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
                CreatedAt = _time.GetLocalNow(),
            }, sending: text);
            return;
        }
        Write(new WorkspaceCommand
        {
            MachineId = _machineId!,
            Kind = WorkspaceCommandKind.Prompt,
            SessionId = Session!.SessionId,
            Folder = Session.Folder,
            Text = text,
            CreatedAt = _time.GetLocalNow(),
        }, sending: text);
    }

    private void Write(WorkspaceCommand command, string status = "Sending to the PC…", string? sending = null)
    {
        _commands.Upsert(command.Id, command);
        _sync?.RequestSync();
        _pending = new SentCommand(command.Id, command.Kind, command.SessionId, sending, _time.GetLocalNow());
        IsSending = true;
        SendFailed = false;
        SendStatus = status;
        if (sending is not null || command.Kind == WorkspaceCommandKind.Answer) Prompt = "";
        _ = WatchTimeoutAsync(command.Id);
        FillTimeline();
    }

    private async Task WatchTimeoutAsync(string id)
    {
        await Task.Delay(AnswerTimeout).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (_pending?.CommandId != id || _results.Get(id) is not null) return;
            _pending = _pending with { Done = true };
            IsSending = false;
            SendFailed = true;
            SendStatus = "The PC did not answer. Is Helm running there, with Workspace Controller on?";
            FillTimeline();
        });
    }

    private void ShowResult()
    {
        if (_pending is not { Done: false } pending || _results.Get(pending.CommandId) is not { } result) return;
        SendFailed = result.Status == WorkspaceCommandStatus.Failed;
        SendStatus = result.Status switch
        {
            WorkspaceCommandStatus.Sending => pending.Text is null ? SendStatus : "The PC is typing it into VS Code…",
            WorkspaceCommandStatus.Sent => result.Error ?? (pending.Text is null ? "Done on the PC." : "Typed into VS Code."),
            WorkspaceCommandStatus.Received => pending.Text is null ? "Done." : "✓ Claude received it",
            _ => result.Error ?? "It could not be sent.",
        };
        if (result.Status != WorkspaceCommandStatus.Sending)
        {
            IsSending = false;
            _pending = pending with { Done = true, Failed = SendFailed };
            if (SendFailed) RevertOverride(pending);
            // A new session: open it once its tab shows up.
            if (pending.Kind == WorkspaceCommandKind.NewSession && result.Status == WorkspaceCommandStatus.Received && result.SessionId is { } sid)
                _awaitSession = sid;
        }
        Refresh();
    }

    private void ClearStatus()
    {
        if (IsSending) return;
        SendStatus = null;
        SendFailed = false;
        _pending = null;
    }

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

            var windows = Host?.Windows ?? [];
            if (!windows.Any(w => SameFolder(w.Folder, _folder)))
            {
                // A notification may name a session of a window by its folder; else the first window.
                _folder = windows.FirstOrDefault(w => records.Any(r => r.SessionId == _sessionId && SameFolder(r.WindowFolder, w.Folder)))?.Folder ?? windows.FirstOrDefault()?.Folder;
            }
            var window = windows.FirstOrDefault(w => SameFolder(w.Folder, _folder));
            var inTabs = window is null ? [] : Ordered(records.Where(r => SameFolder(r.WindowFolder, window.Folder)).ToList(), window);
            if (_awaitSession is not null && inTabs.Any(r => r.SessionId == _awaitSession))
            {
                _sessionId = _awaitSession;
                _awaitSession = null;
                IsNewSession = false;
            }
            if (!IsNewSession && !inTabs.Any(r => r.SessionId == _sessionId))
            {
                // The tab went away (closed) or none was picked: the one that needs you, the one in front, the first.
                var pick = inTabs.FirstOrDefault(r => r.State is SessionState.NeedsPermission or SessionState.Question)
                    ?? inTabs.FirstOrDefault(r => r.SessionId == window?.ActiveSession) ?? inTabs.FirstOrDefault();
                _sessionId = pick?.SessionId;
                if (pick is null && window is not null && _awaitSession is null) IsNewSession = true;
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
        FillTimeline();
        ShowPending();
        if (Sheet is WorkspaceSheet.Model or WorkspaceSheet.Mode) FillChoices();
        if (Sheet == WorkspaceSheet.Agents) FillAgents();
        if (Sheet == WorkspaceSheet.History) FillHistory();
        Notify(nameof(SelectedHost), nameof(HasHosts), nameof(HostOnline), nameof(HostName), nameof(HostStatus), nameof(HasProjects), nameof(SelectedProject),
            nameof(AutoModeAvailable));
        NotifySession();
    }

    /// <summary>The window's sessions in the order of its Claude tabs; others (in the side bar, at work) after them.</summary>
    private static List<WorkspaceSessionRecord> Ordered(List<WorkspaceSessionRecord> records, HostWindowInfo window)
    {
        var order = window.OpenSessions.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
        return records.OrderBy(r => order.TryGetValue(r.SessionId, out var i) ? i : int.MaxValue).ThenBy(r => r.StartedAt).ToList();
    }

    private void NotifySession()
    {
        Notify(nameof(SelectedTab), nameof(Session), nameof(IsSessionOpen), nameof(OpenSessionId), nameof(SessionTitle), nameof(SessionStateText),
            nameof(IsWorking), nameof(NeedsYou), nameof(HasReview), nameof(ReviewLabel), nameof(CanType), nameof(PromptHint), nameof(HistoryTitle));
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
        var rows = new List<TimelineRow>();
        if (Session is { } s)
        {
            rows.AddRange(s.Items.Select(i => new TimelineRow(i)));
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
            if (_pending is { Text: { } text } pending && pending.SessionId == s.SessionId && !Arrived(s, text, pending.At))
                rows.Add(TimelineRow.Sending(text, SendStatus ?? "", SendFailed));
            else if (s.State == SessionState.Working && (rows.Count == 0 || !rows[^1].Running))
                rows.Add(TimelineRow.Working(s.Agents.Count > 0 ? $"Working… {AgentsText} at work" : "Working…"));
        }
        else if (IsNewSession && _pending is { Kind: WorkspaceCommandKind.NewSession, Text: { } first })
        {
            rows.Add(TimelineRow.Sending(first, SendStatus ?? "", SendFailed));
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
        Notify(nameof(ShowStatus));
    }

    /// <summary>The prompt shows in the transcript (it arrived, and Claude has it).</summary>
    private static bool Arrived(WorkspaceSessionRecord s, string text, DateTimeOffset since) =>
        s.Items.Any(i => i.Kind == TranscriptItemKind.User && SessionRegistry.Normalize(i.Text) == SessionRegistry.Normalize(text) && (i.At is null || i.At >= since.AddMinutes(-2)));

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

    private sealed record SentCommand(string CommandId, WorkspaceCommandKind Kind, string? SessionId, string? Text, DateTimeOffset At, bool Done = false, bool Failed = false);

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
