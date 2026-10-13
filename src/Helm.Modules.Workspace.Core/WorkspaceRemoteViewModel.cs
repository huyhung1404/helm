using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Modules.Workspace;

/// <summary>A PC in the picker.</summary>
public sealed class HostChoice(WorkspaceHostRecord host, bool online)
{
    public WorkspaceHostRecord Host { get; } = host;
    public string MachineId => Host.MachineId;
    public bool IsOnline { get; } = online;
    public string Label => IsOnline ? Host.MachineName : $"{Host.MachineName} (offline)";
}

/// <summary>A VS Code window: one tab.</summary>
public sealed class WindowTab(HostWindowInfo window, int attention)
{
    public HostWindowInfo Window { get; } = window;
    public string Name => Window.Name.Length > 0 ? Window.Name : "Window";
    public string Folder => Window.Folder;

    /// <summary>Sessions that finished (unseen) or wait for you.</summary>
    public int Attention { get; } = attention;
    public bool HasAttention => Attention > 0;
}

/// <summary>A session in the list of a window.</summary>
public sealed class RemoteSessionRow(WorkspaceSessionRecord record, bool unseen)
{
    public WorkspaceSessionRecord Record { get; } = record;
    public string SessionId => Record.SessionId;
    public string Title => Record.Title.Length > 0 ? Record.Title : "New conversation";
    public bool Unseen { get; } = unseen;
    public bool IsWorking => Record.State == SessionState.Working;
    public bool NeedsYou => Record.State is SessionState.NeedsPermission or SessionState.Question;
    public bool IsDone => Record.State == SessionState.Done;
    public string StateText => WorkspaceRemoteViewModel.Describe(Record);
    public string Time => Record.UpdatedAt.ToLocalTime().ToString("HH:mm");

    /// <summary>The start of the last answer, for the list.</summary>
    public string Preview => Shorten(Record.LastReply ?? Record.LastPrompt ?? "", 140);

    internal static string Shorten(string text, int max)
    {
        var t = text.ReplaceLineEndings(" ").Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}

/// <summary>One message of the open conversation.</summary>
public sealed class TurnRow(TranscriptTurn turn)
{
    public bool FromUser { get; } = turn.FromUser;
    public bool FromClaude => !FromUser;
    public string Text { get; } = turn.Text;
    public string Who => FromUser ? "You" : "Claude";
    public string Time { get; } = turn.At?.ToLocalTime().ToString("HH:mm") ?? "";
}

/// <summary>Device-local state of the control screen (which PC, what you have seen).</summary>
public sealed class WorkspaceRemoteSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;
    public int Version { get; set; }
    public string LastMachineId { get; set; } = "";

    /// <summary>Session id → the update you last saw (a newer answer is "unseen").</summary>
    public Dictionary<string, DateTimeOffset> Seen { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The phone's control screen for Workspace Controller: pick a PC, each VS Code window is a tab, its Claude sessions
/// with their state; open one to read the conversation and give the next task, or start a new session with a model.
/// Everything goes through Helm Sync; the PC does the typing and reports back.
/// </summary>
public sealed partial class WorkspaceRemoteViewModel : ObservableObject
{
    public const string DefaultModel = "Default";

    /// <summary>A command without any answer from its PC after this long is reported as not delivered.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(45);

    private readonly ISyncedCollection<WorkspaceHostRecord> _hosts;
    private readonly ISyncedCollection<WorkspaceSessionRecord> _sessions;
    private readonly ISyncedCollection<WorkspaceCommand> _commands;
    private readonly ISyncedCollection<WorkspaceResultRecord> _results;
    private readonly ISettingsStore<WorkspaceRemoteSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly ISyncService? _sync;
    private readonly TimeProvider _time;
    private bool _refreshing;
    private string? _pendingCommand;
    private DateTimeOffset _pendingSince;

    [ObservableProperty] private HostChoice? _selectedHost;
    [ObservableProperty] private WindowTab? _selectedTab;
    [ObservableProperty] private RemoteSessionRow? _selectedSession;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSessionCommand))]
    private string _prompt = "";

    [ObservableProperty] private string _newSessionModel = DefaultModel;
    [ObservableProperty] private string? _sendStatus;
    [ObservableProperty] private bool _sendFailed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSessionCommand))]
    private bool _isSending;

    public WorkspaceRemoteViewModel(ISyncedCollection<WorkspaceHostRecord> hosts, ISyncedCollection<WorkspaceSessionRecord> sessions,
        ISyncedCollection<WorkspaceCommand> commands, ISyncedCollection<WorkspaceResultRecord> results, ISettingsStoreFactory settings,
        IUiDispatcher ui, ISyncService? sync = null, TimeProvider? time = null)
    {
        _hosts = hosts;
        _sessions = sessions;
        _commands = commands;
        _results = results;
        _settings = settings.Get<WorkspaceRemoteSettings>(WorkspaceIds.ModuleId + "-remote");
        _ui = ui;
        _sync = sync;
        _time = time ?? TimeProvider.System;
        hosts.Changed += (_, _) => _ui.Post(Refresh);
        sessions.Changed += (_, _) => _ui.Post(Refresh);
        results.Changed += (_, _) => _ui.Post(ShowResult);
        Refresh();
    }

    public ObservableCollection<HostChoice> Hosts { get; } = [];
    public ObservableCollection<WindowTab> Tabs { get; } = [];
    public ObservableCollection<RemoteSessionRow> Sessions { get; } = [];
    public ObservableCollection<TurnRow> Turns { get; } = [];
    public ObservableCollection<SkillInfo> Suggestions { get; } = [];
    public ObservableCollection<string> Models { get; } = [DefaultModel];

    /// <summary>One tap answers for the common next steps.</summary>
    public IReadOnlyList<string> QuickReplies { get; } = ["Continue", "Run the tests", "Commit it"];

    public bool HasHosts => Hosts.Count > 0;
    public bool HasTabs => Tabs.Count > 0;
    public bool HasSessions => Sessions.Count > 0;
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool IsSessionOpen => SelectedSession is not null;
    public bool IsListOpen => SelectedSession is null;
    public bool HostOnline => SelectedHost?.IsOnline == true;

    public string HostStatus => SelectedHost switch
    {
        null => "",
        { IsOnline: true } h => $"Online · {Plural(h.Host.Windows.Count, "VS Code window")}",
        { } h => $"Offline since {h.Host.LastSeen.ToLocalTime():dd/MM HH:mm}. Turn on Workspace Controller in Helm on that PC.",
    };

    public string SessionStateText => SelectedSession is { } s ? Describe(s.Record) : "";
    public string SessionFolder => SelectedSession?.Record.Folder ?? "";

    internal static string Describe(WorkspaceSessionRecord r) => r.State switch
    {
        SessionState.Idle => "Ready",
        SessionState.Working => r.WorkingSince is { } since ? $"Working since {since.ToLocalTime():HH:mm}" : "Working",
        SessionState.NeedsPermission => "Needs your permission" + (r.Waiting is { Length: > 0 } w ? $": {w}" : ""),
        SessionState.Question => "Asked you a question (answer it in VS Code)",
        SessionState.Done => $"Done at {r.UpdatedAt.ToLocalTime():HH:mm}",
        SessionState.Ended => "Ended",
        _ => "",
    };

    partial void OnSelectedHostChanged(HostChoice? value)
    {
        if (_refreshing) return;
        if (value is not null) _settings.Update(s => s.LastMachineId = value.MachineId);
        SelectedTab = null;
        SelectedSession = null;
        Refresh();
    }

    partial void OnSelectedTabChanged(WindowTab? value)
    {
        if (_refreshing) return;
        SelectedSession = null;
        Refresh();
    }

    partial void OnSelectedSessionChanged(RemoteSessionRow? value)
    {
        OnPropertyChanged(nameof(IsSessionOpen));
        OnPropertyChanged(nameof(IsListOpen));
        OnPropertyChanged(nameof(SessionStateText));
        OnPropertyChanged(nameof(SessionFolder));
        FillTurns();
        SendCommand.NotifyCanExecuteChanged();
        // Seen now: the dot on the row and the count on the tab go away.
        if (!_refreshing && value is not null && MarkSeen(value.Record)) Refresh();
    }

    partial void OnPromptChanged(string value)
    {
        Suggestions.Clear();
        var t = value.TrimStart();
        if (t.StartsWith('/') && !t.Contains(' ') && SelectedHost is { } h)
        {
            var typed = t[1..];
            foreach (var skill in h.Host.Skills.Where(s => s.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Take(8))
                Suggestions.Add(skill);
        }
        OnPropertyChanged(nameof(HasSuggestions));
    }

    /// <summary>Back from the conversation to the list (Android back).</summary>
    public bool CloseSession()
    {
        if (SelectedSession is null) return false;
        SelectedSession = null;
        return true;
    }

    [RelayCommand]
    private void PickSuggestion(SkillInfo? skill)
    {
        if (skill is not null) Prompt = "/" + skill.Name + " ";
    }

    [RelayCommand]
    private void QuickReply(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Prompt = text;
    }

    private bool CanSend() => !IsSending && HostOnline && SelectedSession is not null && !string.IsNullOrWhiteSpace(Prompt);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send() => Write(new WorkspaceCommand
    {
        MachineId = SelectedHost!.MachineId,
        Kind = WorkspaceCommandKind.Prompt,
        SessionId = SelectedSession!.SessionId,
        Folder = SelectedSession.Record.Folder,
        Text = Prompt.Trim(),
        CreatedAt = _time.GetLocalNow(),
    });

    private bool CanStartSession() => !IsSending && HostOnline && SelectedTab is not null && !string.IsNullOrWhiteSpace(Prompt);

    [RelayCommand(CanExecute = nameof(CanStartSession))]
    private void StartSession() => Write(new WorkspaceCommand
    {
        MachineId = SelectedHost!.MachineId,
        Kind = WorkspaceCommandKind.NewSession,
        Folder = SelectedTab!.Folder,
        Text = Prompt.Trim(),
        Model = NewSessionModel == DefaultModel ? null : NewSessionModel,
        CreatedAt = _time.GetLocalNow(),
    });

    private void Write(WorkspaceCommand command)
    {
        _commands.Upsert(command.Id, command);
        _sync?.RequestSync();
        _pendingCommand = command.Id;
        _pendingSince = _time.GetLocalNow();
        IsSending = true;
        SendFailed = false;
        SendStatus = "Sending to the PC…";
        Prompt = "";
        _ = WatchTimeoutAsync(command.Id);
    }

    private async Task WatchTimeoutAsync(string id)
    {
        await Task.Delay(AnswerTimeout).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (_pendingCommand != id || _results.Get(id) is not null) return;
            _pendingCommand = null;
            IsSending = false;
            SendFailed = true;
            SendStatus = "The PC did not answer. Is Helm running there, with Workspace Controller on?";
        });
    }

    private void ShowResult()
    {
        if (_pendingCommand is not { } id || _results.Get(id) is not { } result) return;
        SendFailed = result.Status == WorkspaceCommandStatus.Failed;
        SendStatus = result.Status switch
        {
            WorkspaceCommandStatus.Sending => "The PC is typing it into VS Code…",
            WorkspaceCommandStatus.Sent => result.Error ?? "Typed into VS Code.",
            WorkspaceCommandStatus.Received => "Claude received it.",
            _ => result.Error ?? "It could not be sent.",
        };
        if (result.Status == WorkspaceCommandStatus.Sending) return;
        IsSending = false;
        _pendingCommand = null;
        // A new session: open it once its record arrives.
        if (result.Status == WorkspaceCommandStatus.Received && SelectedSession is null && result.SessionId is { } sid)
        {
            Refresh();
            SelectedSession = Sessions.FirstOrDefault(s => s.SessionId == sid);
        }
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var now = _time.GetLocalNow();
            var hosts = _hosts.All().Select(i => i.Value).OrderByDescending(h => h.IsOnline(now)).ThenBy(h => h.MachineName, StringComparer.CurrentCultureIgnoreCase).ToList();
            var hostId = SelectedHost?.MachineId ?? _settings.Current.LastMachineId;
            Hosts.Clear();
            foreach (var h in hosts) Hosts.Add(new HostChoice(h, h.IsOnline(now)));
            SelectedHost = Hosts.FirstOrDefault(h => h.MachineId == hostId) ?? Hosts.FirstOrDefault();

            var records = SelectedHost is null ? [] : _sessions.All().Select(i => i.Value).Where(r => r.MachineId == SelectedHost.MachineId).ToList();
            var tabFolder = SelectedTab?.Folder;
            Tabs.Clear();
            foreach (var w in SelectedHost?.Host.Windows ?? [])
            {
                var inTab = records.Where(r => SameFolder(r.WindowFolder, w.Folder)).ToList();
                Tabs.Add(new WindowTab(w, inTab.Count(r => r.State is SessionState.NeedsPermission or SessionState.Question || IsUnseen(r))));
            }
            SelectedTab = Tabs.FirstOrDefault(t => SameFolder(t.Folder, tabFolder)) ?? Tabs.FirstOrDefault();

            var sessionId = SelectedSession?.SessionId;
            Sessions.Clear();
            if (SelectedTab is not null)
            {
                foreach (var r in records.Where(r => SameFolder(r.WindowFolder, SelectedTab.Folder)).OrderByDescending(r => r.UpdatedAt))
                    Sessions.Add(new RemoteSessionRow(r, IsUnseen(r)));
            }
            var stillOpen = Sessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (stillOpen is not null || sessionId is null) SelectedSession = stillOpen;

            Models.Clear();
            Models.Add(DefaultModel);
            foreach (var m in SelectedHost?.Host.Models ?? []) Models.Add(m);
            if (!Models.Contains(NewSessionModel)) NewSessionModel = DefaultModel;
        }
        finally
        {
            _refreshing = false;
        }
        // An answer that arrives while the conversation is open is seen at once (the rows show it next time).
        if (SelectedSession is { } open) MarkSeen(open.Record);
        FillTurns();
        OnPropertyChanged(nameof(HasHosts));
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HostOnline));
        OnPropertyChanged(nameof(HostStatus));
        OnPropertyChanged(nameof(SessionStateText));
        SendCommand.NotifyCanExecuteChanged();
        StartSessionCommand.NotifyCanExecuteChanged();
    }

    private void FillTurns()
    {
        Turns.Clear();
        if (SelectedSession is not { } s) return;
        var turns = s.Record.Recent.Count > 0
            ? s.Record.Recent
            : new[] { s.Record.LastPrompt is { } p ? new TranscriptTurn(true, p, null) : null, s.Record.LastReply is { } r ? new TranscriptTurn(false, r, null) : null }
                .OfType<TranscriptTurn>().ToList();
        foreach (var t in turns) Turns.Add(new TurnRow(t));
    }

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

    private static bool SameFolder(string? a, string? b) =>
        a is not null && b is not null && SessionRegistry.FolderKey(a) == SessionRegistry.FolderKey(b);

    private static string Plural(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
