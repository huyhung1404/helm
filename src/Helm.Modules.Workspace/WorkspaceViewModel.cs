using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;

namespace Helm.Modules.Workspace;

/// <summary>One Claude session as this PC sees it. The phone steers it; here you only connect or disconnect it.</summary>
public sealed class SessionRow(AgentSession session, bool connected = true, Action<bool>? setConnected = null)
{
    private bool _connected = connected;

    public AgentSession Session { get; } = session;

    /// <summary>The phone sees and steers it. Hidden for sessions Helm cannot type into (outside VS Code).</summary>
    public bool IsConnected
    {
        get => _connected;
        set
        {
            if (_connected == value) return;
            _connected = value;
            setConnected?.Invoke(value);
        }
    }

    public bool CanConnect => setConnected is not null;

    public string Title => Session.Title.Length > 0 ? Session.Title : "New conversation";

    /// <summary>"Working · 11:41".</summary>
    public string Detail => $"{StateText} · {Session.UpdatedAt.ToLocalTime():HH:mm}";

    public string StateText => Session.State switch
    {
        SessionState.Idle => "Ready",
        SessionState.Working => "Working",
        SessionState.NeedsPermission => "Needs permission",
        SessionState.Question => "Asked a question",
        SessionState.Done => "Done",
        SessionState.Ended => "Ended",
        _ => "",
    };
}

/// <summary>An app window connected to this host (a VS Code window today; Unity and others later) with its sessions.</summary>
public sealed class HostWindowRow(string name, string detail, IReadOnlyList<SessionRow> sessions, bool connected = true, Action<bool>? setConnected = null)
{
    private bool _connected = connected;

    public string Name { get; } = name;

    /// <summary>The whole window: all its sessions, also the ones opened later (unless one is set on its own).</summary>
    public bool IsConnected
    {
        get => _connected;
        set
        {
            if (_connected == value) return;
            _connected = value;
            setConnected?.Invoke(value);
        }
    }

    /// <summary>"c:\workpp · 2 Claude tabs".</summary>
    public string Detail { get; } = detail;

    public IReadOnlyList<SessionRow> Sessions { get; } = sessions;

    public bool HasSessions => Sessions.Count > 0;
}

/// <summary>
/// Workspace Controller's settings page on the PC. The PC is only the host: this shows that it is listening, that
/// Claude's hooks and Helm Bridge are in place, and which sessions it sees. Steering happens on the phone.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject
{
    private readonly ISettingsStore<WorkspaceSettings> _store;
    private readonly IUiDispatcher _ui;
    private readonly IDeviceInfo? _device;
    private bool _loading;

    [ObservableProperty] private bool _manageClaudeHooks;
    [ObservableProperty] private bool _manageBridgeExtension;
    [ObservableProperty] private bool _connectNewSessions;
    [ObservableProperty] private string _listeningText = "";
    [ObservableProperty] private string _hooksText = "";
    [ObservableProperty] private string _bridgeText = "";
    [ObservableProperty] private string _sessionsText = "";
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
        ConnectNewSessions = _store.Current.ConnectNewSessions;
        _loading = false;
        module.Changed += (_, _) => _ui.Post(Refresh);
        Refresh();
    }

    public WorkspaceModule Module { get; }

    public string MachineName => _device?.DeviceName ?? Environment.MachineName;

    /// <summary>The connected VS Code windows, each with its Claude sessions (newest first).</summary>
    public ObservableCollection<HostWindowRow> VsCodeWindows { get; } = [];

    /// <summary>Sessions outside a connected VS Code window (a terminal, another editor): seen, not steerable.</summary>
    public ObservableCollection<SessionRow> OtherSessions { get; } = [];

    public bool HasVsCodeWindows => VsCodeWindows.Count > 0;

    public bool HasOtherSessions => OtherSessions.Count > 0;

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

    partial void OnConnectNewSessionsChanged(bool value)
    {
        if (_loading) return;
        _store.Update(s => s.ConnectNewSessions = value);
        Refresh();
    }

    [RelayCommand]
    private void InstallHooks() => Module.InstallHooks();

    [RelayCommand]
    private Task InstallBridgeAsync() => Module.EnsureBridgeAsync();

    private void Refresh()
    {
        var engine = Module.Engine;
        IsRunning = engine is not null;

        VsCodeWindows.Clear();
        OtherSessions.Clear();
        var windows = engine?.Windows() ?? [];
        foreach (var w in windows)
        {
            var info = w.Connection.Info;
            var folders = info.Folders.Count == 0 ? "No folder open" : string.Join(", ", info.Folders);
            var tabs = info.ClaudeTabs == 1 ? "1 Claude tab" : $"{info.ClaudeTabs} Claude tabs";
            var folder = info.Folders.FirstOrDefault();
            var windowConnected = folder is null || (_store.Current.WindowLinks.TryGetValue(SessionRegistry.FolderKey(folder), out var linked) ? linked : _store.Current.ConnectNewSessions);
            var sessions = w.Sessions.OrderByDescending(s => s.UpdatedAt)
                .Select(s => new SessionRow(s, engine!.IsConnected(s), on => { engine.SetSessionLink(s.SessionId, on); }))
                .ToList();
            VsCodeWindows.Add(new HostWindowRow(w.Name.Length > 0 ? w.Name : "Empty window", $"{folders} · {tabs}", sessions,
                windowConnected, folder is null ? null : on => engine!.SetWindowLink(folder, on)));
        }
        foreach (var s in engine?.OtherSessions() ?? []) OtherSessions.Add(new SessionRow(s));
        OnPropertyChanged(nameof(HasVsCodeWindows));
        OnPropertyChanged(nameof(HasOtherSessions));
        var all = windows.SelectMany(w => w.Sessions).Concat(OtherSessions.Select(r => r.Session)).ToList();

        ListeningText = engine is null
            ? "Off. Turn the tool on to let your phone reach this PC."
            : $"This PC is “{MachineName}”. Helm listens for Claude Code on 127.0.0.1:{engine.HookPort}.";
        HooksText = !_store.Current.ManageClaudeHooks
            ? "Off: Helm does not add its hooks, so it cannot see your Claude sessions."
            : Module.HooksProblem ?? "Helm's hooks are in Claude Code's settings. Your other hooks stay as they are.";
        var connected = windows.Count;
        BridgeText = !_store.Current.ManageBridgeExtension
            ? "Off: Helm does not install Helm Bridge."
            : Module.BridgeProblem ?? (BridgeInstaller.IsInstalled()
                ? $"Helm Bridge {BridgeInstaller.Version} is installed. {Plural(connected, "VS Code window")} connected."
                : "Installing Helm Bridge into VS Code…");
        SessionsText = engine is null
            ? "Turn the tool on to see the VS Code windows connected to this PC."
            : $"{Plural(windows.Count, "window")} connected · {Plural(all.Count, "Claude session")}, {all.Count(s => s.State == SessionState.Working)} working.";
    }

    private static string Plural(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
