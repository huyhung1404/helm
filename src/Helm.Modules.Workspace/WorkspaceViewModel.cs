using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;

namespace Helm.Modules.Workspace;

/// <summary>One Claude session in the list.</summary>
public sealed class SessionRow(AgentSession session, bool canSend)
{
    public AgentSession Session { get; } = session;
    public string SessionId => Session.SessionId;
    public bool CanSend { get; } = canSend;

    public string Title => Session.Title.Length > 0 ? Session.Title : "New conversation";

    public string Folder => Session.Folder;

    public string StateText => Session.State switch
    {
        SessionState.Idle => "Ready",
        SessionState.Working => "Working",
        SessionState.NeedsPermission => "Needs your permission" + (Session.Waiting is { Length: > 0 } w ? $" · {w}" : ""),
        SessionState.Question => "Asked you a question",
        SessionState.Done => "Done",
        SessionState.Ended => "Ended",
        _ => "",
    };

    public bool IsWorking => Session.State == SessionState.Working;
    public bool NeedsYou => Session.NeedsYou;
    public bool IsDone => Session.State == SessionState.Done;

    public string LastPrompt => Session.LastPrompt ?? "";
    public string LastReply => Session.LastReply ?? "";
    public bool HasReply => !string.IsNullOrWhiteSpace(Session.LastReply);
    public string Updated => Session.UpdatedAt.ToLocalTime().ToString("HH:mm");
}

/// <summary>A VS Code window with its sessions.</summary>
public sealed class WindowRow(string name, string folders, IReadOnlyList<SessionRow> sessions, BridgeConnection? connection)
{
    public string Name { get; } = name;
    public string Folders { get; } = folders;
    public IReadOnlyList<SessionRow> Sessions { get; } = sessions;
    public BridgeConnection? Connection { get; } = connection;
    public bool HasSessions => Sessions.Count > 0;

    /// <summary>The window's first folder: where a new session opens.</summary>
    public string? Folder => Connection?.Info.Folders.FirstOrDefault();
}

/// <summary>Workspace Controller on the PC: the session list and composer (content page) and the options (settings page).</summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    public const string DefaultModel = "Default";

    private readonly ISettingsStore<WorkspaceSettings> _store;
    private readonly IUiDispatcher _ui;
    private readonly IDeviceInfo? _device;
    private bool _loading;

    [ObservableProperty] private bool _manageClaudeHooks;
    [ObservableProperty] private bool _manageBridgeExtension;
    [ObservableProperty] private bool _notifyOnPc;

    [ObservableProperty] private SessionRow? _selectedSession;
    [ObservableProperty] private WindowRow? _newSessionWindow;
    [ObservableProperty] private string _newSessionModel = DefaultModel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSessionCommand))]
    private string _prompt = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSessionCommand))]
    private bool _isSending;

    [ObservableProperty] private string? _sendStatus;
    [ObservableProperty] private bool _sendFailed;

    [ObservableProperty] private string _listeningText = "";
    [ObservableProperty] private string _hooksText = "";
    [ObservableProperty] private string _bridgeText = "";
    [ObservableProperty] private bool _isRunning;

    public WorkspaceViewModel(WorkspaceModule module, IUiDispatcher ui, IDeviceInfo? device = null)
    {
        Module = module;
        _store = module.Settings;
        _ui = ui;
        _device = device;
        _loading = true;
        ManageClaudeHooks = _store.Current.ManageClaudeHooks;
        ManageBridgeExtension = _store.Current.ManageBridgeExtension;
        NotifyOnPc = _store.Current.NotifyOnPc;
        _loading = false;
        module.Changed += (_, _) => _ui.Post(Refresh);
        Refresh();
    }

    public WorkspaceModule Module { get; }

    public string MachineName => _device?.DeviceName ?? Environment.MachineName;

    public ObservableCollection<WindowRow> Windows { get; } = [];

    /// <summary>Sessions outside a VS Code window with Helm Bridge (a terminal, another editor): shown, not steerable.</summary>
    public ObservableCollection<SessionRow> OtherSessions { get; } = [];

    public bool HasOtherSessions => OtherSessions.Count > 0;

    public bool HasWindows => Windows.Count > 0;

    public bool HasSelection => SelectedSession is not null;

    public IReadOnlyList<string> Models { get; } = [DefaultModel, "opus", "sonnet", "haiku"];

    partial void OnManageClaudeHooksChanged(bool value)
    {
        if (_loading) return;
        _store.Update(s => s.ManageClaudeHooks = value);
        if (value) Module.InstallHooks();
        else Module.RemoveHooks();
    }

    partial void OnManageBridgeExtensionChanged(bool value)
    {
        if (_loading) return;
        _store.Update(s => s.ManageBridgeExtension = value);
        if (value) _ = Module.EnsureBridgeAsync();
    }

    partial void OnNotifyOnPcChanged(bool value)
    {
        if (!_loading) _store.Update(s => s.NotifyOnPc = value);
    }

    partial void OnSelectedSessionChanged(SessionRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        SendCommand.NotifyCanExecuteChanged();
    }

    private bool CanSend() => !IsSending && SelectedSession is { CanSend: true } && !string.IsNullOrWhiteSpace(Prompt) && Module.Engine is not null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync() => RunAsync(new WorkspaceCommand
    {
        MachineId = Module.Engine!.MachineId,
        Kind = WorkspaceCommandKind.Prompt,
        SessionId = SelectedSession!.SessionId,
        Folder = SelectedSession.Folder,
        Text = Prompt,
        CreatedAt = DateTimeOffset.Now,
    });

    private bool CanStartSession() => !IsSending && !string.IsNullOrWhiteSpace(Prompt) && Module.Engine is not null;

    [RelayCommand(CanExecute = nameof(CanStartSession))]
    private Task StartSessionAsync()
    {
        var window = NewSessionWindow ?? Windows.FirstOrDefault(w => w.Connection is not null);
        if (window?.Folder is not { } folder)
        {
            SendFailed = true;
            SendStatus = "Open a folder in VS Code (with Helm Bridge) first.";
            return Task.CompletedTask;
        }
        return RunAsync(new WorkspaceCommand
        {
            MachineId = Module.Engine!.MachineId,
            Kind = WorkspaceCommandKind.NewSession,
            Folder = folder,
            Text = Prompt,
            Model = NewSessionModel == DefaultModel ? null : NewSessionModel,
            CreatedAt = DateTimeOffset.Now,
        });
    }

    private async Task RunAsync(WorkspaceCommand command)
    {
        if (Module.Engine is not { } engine) return;
        IsSending = true;
        SendFailed = false;
        SendStatus = "Typing it into VS Code…";
        try
        {
            var result = await engine.RunAsync(command, CancellationToken.None).ConfigureAwait(true);
            SendFailed = result.Status == WorkspaceCommandStatus.Failed;
            SendStatus = result.Status switch
            {
                WorkspaceCommandStatus.Received => "Claude received it.",
                WorkspaceCommandStatus.Sent => result.Error,
                _ => result.Error ?? "It could not be sent.",
            };
            if (!SendFailed) Prompt = "";
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private void InstallHooks() => Module.InstallHooks();

    [RelayCommand]
    private void RemoveHooks()
    {
        Module.RemoveHooks();
        _loading = true;
        ManageClaudeHooks = false;
        _loading = false;
        _store.Update(s => s.ManageClaudeHooks = false);
    }

    [RelayCommand]
    private Task InstallBridgeAsync() => Module.EnsureBridgeAsync();

    private void Refresh()
    {
        var engine = Module.Engine;
        IsRunning = engine is not null;
        var selected = SelectedSession?.SessionId;
        var newWindow = NewSessionWindow?.Name;

        Windows.Clear();
        OtherSessions.Clear();
        if (engine is not null)
        {
            foreach (var w in engine.Windows())
            {
                var rows = w.Sessions.Select(s => new SessionRow(s, canSend: s.State != SessionState.Ended)).ToList();
                Windows.Add(new WindowRow(w.Name.Length > 0 ? w.Name : "(no folder)", string.Join(", ", w.Connection.Info.Folders), rows, w.Connection));
            }
            foreach (var s in engine.OtherSessions()) OtherSessions.Add(new SessionRow(s, canSend: false));
        }
        SelectedSession = Windows.SelectMany(w => w.Sessions).Concat(OtherSessions).FirstOrDefault(s => s.SessionId == selected);
        NewSessionWindow = Windows.FirstOrDefault(w => w.Name == newWindow) ?? Windows.FirstOrDefault();
        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(HasOtherSessions));

        ListeningText = engine is null
            ? "Off."
            : $"Listening for Claude Code on 127.0.0.1:{engine.HookPort}. This PC: {MachineName}.";
        HooksText = !_store.Current.ManageClaudeHooks
            ? "Helm does not manage its hooks. Without them Helm cannot see your Claude sessions."
            : Module.HooksProblem ?? "Helm's hooks are in Claude Code's settings (your other hooks stay as they are).";
        BridgeText = !_store.Current.ManageBridgeExtension
            ? "Helm does not install Helm Bridge."
            : Module.BridgeProblem ?? (BridgeInstaller.IsInstalled()
                ? $"Helm Bridge {BridgeInstaller.Version} is installed. {Plural(Windows.Count(w => w.Connection is not null), "VS Code window")} connected."
                : "Installing Helm Bridge into VS Code…");
        SendCommand.NotifyCanExecuteChanged();
        StartSessionCommand.NotifyCanExecuteChanged();
    }

    private static string Plural(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
