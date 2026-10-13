using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Helm.Core.Desktop;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>
/// A VS Code window and the Claude sessions whose folder is in it; <see cref="OpenSessions"/> are those open as Claude
/// tabs, in tab order (null when the window's bridge is too old to tell).
/// </summary>
public sealed record WorkspaceWindow(BridgeConnection Connection, IReadOnlyList<AgentSession> Sessions, IReadOnlyList<string>? OpenSessions = null, string? ActiveSession = null)
{
    public string Name => Connection.Info.Name;

    /// <summary>Its first folder (where a new session opens; the phone's key for the window).</summary>
    public string Folder => Connection.Info.Folders.FirstOrDefault() ?? "";
}

/// <summary>
/// The running Workspace Controller on this PC: hears Claude Code's hooks (<see cref="HookListener"/>), keeps the
/// sessions (<see cref="SessionRegistry"/>), knows the VS Code windows (<see cref="BridgeHost"/>) and runs commands
/// (<see cref="PromptSender"/>). Started and stopped by the module.
/// </summary>
public sealed class WorkspaceEngine : IAsyncDisposable
{
    /// <summary>How long a typed prompt may take to show up in Claude's UserPromptSubmit hook.</summary>
    public static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(20);

    private readonly ISettingsStore<WorkspaceSettings> _settings;
    private readonly PromptSender _sender;
    private readonly string? _projectsRoot;
    private readonly ConcurrentDictionary<string, byte> _seeded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _receipts = new();
    private readonly ConcurrentDictionary<string, byte> _titleReads = new();
    private readonly ConcurrentDictionary<string, Held> _held = new();
    private readonly IUserActivity? _activity;
    private long _lastRealInput;
    private HookListener? _hooks;
    private BridgeHost? _bridge;

    private WorkspaceEngine(ISettingsStore<WorkspaceSettings> settings, SessionRegistry registry, PromptSender sender,
        string? projectsRoot, IUserActivity? activity, ILogger logger, TimeProvider time)
    {
        _activity = activity;
        _lastRealInput = activity?.LastInputTick ?? Environment.TickCount64;
        _settings = settings;
        Registry = registry;
        _sender = sender;
        _projectsRoot = projectsRoot;
        History = new ClaudeHistory(projectsRoot);
        _logger = logger;
        _time = time;
        registry.Changed += OnSessionChanged;
        registry.PromptReceived += (_, r) =>
        {
            if (_receipts.TryRemove(r.CommandId, out var waiting)) waiting.TrySetResult(r.SessionId);
        };
    }

    public SessionRegistry Registry { get; }

    /// <summary>Claude's records of earlier sessions, per folder.</summary>
    public ClaudeHistory History { get; }

    public string MachineId => Registry.MachineId;

    public int HookPort => _hooks?.Port ?? 0;

    public string? BridgePipe => _bridge?.PipeName;

    /// <summary>Sessions, windows or their state changed. Raised off the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The phone can answer (the relay runs). Without it a permission request is never held: VS Code asks at once.
    /// </summary>
    public bool PhoneLinked { get; set; }

    /// <summary>Starts listening. A taken hook port is replaced by a free one (and Claude's settings follow).</summary>
    public static WorkspaceEngine Start(ISettingsStore<WorkspaceSettings> settings, HelmPaths paths, PromptSender sender,
        ILogger logger, TimeProvider? time = null, string? bridgePipe = null, string? projectsRoot = null, IUserActivity? activity = null)
    {
        EnsureIdentity(settings);
        var s = settings.Current;
        var engine = new WorkspaceEngine(settings, new SessionRegistry(s.InstallId, time), sender, projectsRoot, activity, logger, time ?? TimeProvider.System);
        try
        {
            engine._hooks = HookListener.Start(s.HookPort, s.HookToken, engine.HandleHookAsync, logger);
        }
        catch (SocketException ex) when (s.HookPort != 0)
        {
            logger.LogWarning(ex, "Workspace: hook port {Port} is taken, picking another", s.HookPort);
            engine._hooks = HookListener.Start(0, s.HookToken, engine.HandleHookAsync, logger);
        }
        if (engine._hooks.Port != s.HookPort) settings.Update(x => x.HookPort = engine._hooks.Port);
        engine._bridge = new BridgeHost(bridgePipe ?? WorkspaceEndpoint.PipeName(paths), logger);
        engine._bridge.WindowsChanged += (_, _) =>
        {
            engine.SeedNewFolders();
            engine.ResolveTabsLater();
            engine.Changed?.Invoke(engine, EventArgs.Empty);
        };
        return engine;
    }

    /// <summary>The install id, hook token (and nothing else) are created once and kept.</summary>
    private static void EnsureIdentity(ISettingsStore<WorkspaceSettings> settings)
    {
        var s = settings.Current;
        if (s.InstallId.Length > 0 && s.HookToken.Length >= 32) return;
        settings.Update(x =>
        {
            if (x.InstallId.Length == 0) x.InstallId = Guid.NewGuid().ToString("N");
            if (x.HookToken.Length < 32) x.HookToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        });
    }

    /// <summary>
    /// Writes Helm's hooks into Claude Code's user settings (other hooks stay). Returns null when they are in place,
    /// else why not. Claude re-reads its settings by itself, so running sessions pick them up.
    /// </summary>
    public string? InstallClaudeHooks(string? settingsPath = null)
    {
        var path = settingsPath ?? ClaudeHookConfig.UserSettingsPath();
        try
        {
            var s = _settings.Current;
            ClaudeHookConfig.Rewrite(path, json => ClaudeHookConfig.IsInstalled(json, HookPort, s.HookToken) ? null : ClaudeHookConfig.Install(json, HookPort, s.HookToken));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Workspace: Claude's hooks could not be written to {Path}", path);
            return ex is JsonException
                ? $"Claude Code's settings ({path}) are not valid JSON, so Helm left them alone."
                : $"Claude Code's settings ({path}) could not be changed: {ex.Message}";
        }
    }

    /// <summary>Takes Helm's hooks out of Claude Code's user settings. Returns null when done.</summary>
    public static string? RemoveClaudeHooks(ILogger logger, string? settingsPath = null)
    {
        var path = settingsPath ?? ClaudeHookConfig.UserSettingsPath();
        try
        {
            ClaudeHookConfig.Rewrite(path, ClaudeHookConfig.Remove);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Workspace: Claude's hooks could not be removed from {Path}", path);
            return $"Helm's hooks could not be removed from {path}: {ex.Message}";
        }
    }

    /// <summary>
    /// The connected VS Code windows, each with its sessions (a session belongs to the window that has its folder) and
    /// the ones open as Claude tabs.
    /// </summary>
    public IReadOnlyList<WorkspaceWindow> Windows()
    {
        var sessions = Registry.Sessions;
        return (_bridge?.Windows ?? [])
            .OrderBy(w => w.Info.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(w =>
            {
                var inWindow = sessions.Where(s => Contains(w, s.Folder)).ToList();
                if (w.Info.Tabs is not { } tabs) return new WorkspaceWindow(w, inWindow);
                var (open, active) = MatchTabs(tabs, inWindow);
                return new WorkspaceWindow(w, inWindow, open.OfType<string>().ToList(), active);
            })
            .ToList();
    }

    private static (IReadOnlyList<string?> Sessions, string? Active) MatchTabs(IReadOnlyList<BridgeTab> tabs, IReadOnlyList<AgentSession> sessions) =>
        ClaudeTabMatch.Match(tabs.Select(t => (t.Label, t.Active)).ToList(),
            sessions.Where(s => s.State != SessionState.Ended).Select(s => new ClaudeTabMatch.Candidate(s.SessionId, s.Title, s.LastPrompt, s.UpdatedAt)).ToList());

    /// <summary>
    /// Claude tabs whose session Helm does not know yet (opened before Helm started, days ago) are looked up in
    /// Claude's records by their name and listed.
    /// </summary>
    private void ResolveTabsLater()
    {
        var windows = _bridge?.Windows ?? [];
        var sessions = Registry.Sessions;
        var missing = new List<(string Folder, string Label)>();
        foreach (var w in windows)
        {
            if (w.Info.Tabs is not { } tabs) continue;
            var inWindow = sessions.Where(s => Contains(w, s.Folder)).ToList();
            var (open, _) = MatchTabs(tabs, inWindow);
            for (var i = 0; i < tabs.Count; i++)
                if (open[i] is null && tabs[i].Label != ClaudeTabMatch.Untitled) missing.AddRange(w.Info.Folders.Select(f => (f, tabs[i].Label)));
        }
        if (missing.Count == 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var added = false;
                foreach (var group in missing.GroupBy(m => SessionRegistry.FolderKey(m.Folder)))
                {
                    var folder = group.First().Folder;
                    var labels = group.Select(m => m.Label).ToHashSet(StringComparer.Ordinal);
                    foreach (var entry in History.For(folder).Where(h => labels.Contains(ClaudeTabMatch.Label(h.Title))))
                    {
                        labels.Remove(ClaudeTabMatch.Label(entry.Title));
                        added |= SeedSession(folder, entry.SessionId);
                    }
                }
                if (added) Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workspace: Claude tabs could not be matched to sessions");
            }
        });
    }

    /// <summary>Lists one session from Claude's records (it is open in a tab, or about to be). False when known already.</summary>
    internal bool SeedSession(string folder, string sessionId)
    {
        if (Registry.Find(sessionId) is not null || History.TranscriptOf(folder, sessionId) is not { } path) return false;
        var tail = TranscriptReader.Read(path, maxTurns: 6);
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return Registry.Seed(new AgentSession
        {
            MachineId = Registry.MachineId,
            SessionId = sessionId,
            Folder = folder,
            TranscriptPath = path,
            Title = tail.Title,
            State = tail.Turns.Count > 0 && !tail.Turns[^1].FromUser ? SessionState.Done : SessionState.Idle,
            LastPrompt = tail.Turns.LastOrDefault(t => t.FromUser)?.Text,
            LastReply = tail.Turns.LastOrDefault(t => !t.FromUser)?.Text,
            StartedAt = written,
            UpdatedAt = written.ToLocalTime(),
            FromHistory = true,
        });
    }

    /// <summary>Sessions no connected VS Code window has (a terminal, another editor, a window without the bridge).</summary>
    public IReadOnlyList<AgentSession> OtherSessions()
    {
        var windows = _bridge?.Windows ?? [];
        return Registry.Sessions.Where(s => !windows.Any(w => Contains(w, s.Folder))).ToList();
    }

    /// <summary>Runs one command and reports how far it got. Never throws.</summary>
    public async Task<WorkspaceCommandResult> RunAsync(WorkspaceCommand command, CancellationToken ct)
    {
        if (command.MachineId != MachineId)
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "This command is for another PC.");
        if (command.CreatedAt != default && _time.GetLocalNow() - command.CreatedAt > WorkspaceCommand.MaxAge)
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "The command is too old; it was not typed.");
        if (command.NeedsText && string.IsNullOrWhiteSpace(command.Text))
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "The prompt is empty.");

        BridgeConnection? window;
        string? sessionId = null;
        string folder;
        switch (command.Kind)
        {
            case WorkspaceCommandKind.Answer:
                return Answer(command);
            case WorkspaceCommandKind.SetModel or WorkspaceCommandKind.SetEffort or WorkspaceCommandKind.SetMode or WorkspaceCommandKind.CloseTab:
                return await ChangeSessionAsync(command, ct).ConfigureAwait(false);
            case WorkspaceCommandKind.Reopen:
                return await ReopenAsync(command, ct).ConfigureAwait(false);
        }
        if (command.Kind == WorkspaceCommandKind.Prompt)
        {
            if (command.SessionId is null || Registry.Find(command.SessionId) is not { } session)
                return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "That Claude session is not open on this PC.");
            if (session.State == SessionState.Ended)
                return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "That Claude session has ended.");
            if (!IsConnected(session))
                return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "That session is disconnected from Helm on the PC.");
            sessionId = session.SessionId;
            folder = session.Folder;
            window = WindowFor(folder);
        }
        else
        {
            folder = command.Folder;
            window = WindowFor(folder);
            if (window is not null && _settings.Current.WindowLinks.TryGetValue(SessionRegistry.FolderKey(window.Info.Folders.First(f => IsUnder(folder, f))), out var linked) && !linked)
                return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "That VS Code window is disconnected from Helm on the PC.");
        }
        if (window is null)
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "No VS Code window with Helm Bridge has that folder open.");

        var receipt = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _receipts[command.Id] = receipt;
        Registry.ExpectPrompt(command.Id, sessionId, folder, command.Text, ReceiptTimeout + TimeSpan.FromSeconds(10));
        try
        {
            var setup = command.Kind == WorkspaceCommandKind.NewSession
                ? new NewSessionSetup(command.Model, command.Effort, command.Mode is { } m ? Math.Max(0, PermissionModes.Presses(PermissionModes.Manual, m, AutoModeAvailable)) : 0)
                : null;
            await _sender.SendAsync(window, sessionId, command.Text, setup, ct, receipt.Task).ConfigureAwait(false);
        }
        catch (PromptSendException ex)
        {
            Forget(command.Id);
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, ex.Message, sessionId);
        }
        catch (OperationCanceledException)
        {
            Forget(command.Id);
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "Stopped.", sessionId);
        }
        try
        {
            var arrived = await receipt.Task.WaitAsync(ReceiptTimeout, ct).ConfigureAwait(false);
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Received, null, arrived);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            Forget(command.Id);
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Sent,
                "Typed and sent, but Claude did not confirm it yet (are Helm's hooks installed?).", sessionId);
        }
    }

    /// <summary>Slash commands Claude takes without a reply hook: give them this long, then report them as sent.</summary>
    private static readonly TimeSpan SlashSettle = TimeSpan.FromMilliseconds(1500);

    /// <summary>Model, effort, mode or closing the tab of one open session.</summary>
    private async Task<WorkspaceCommandResult> ChangeSessionAsync(WorkspaceCommand command, CancellationToken ct)
    {
        if (command.SessionId is null || Registry.Find(command.SessionId) is not { } session)
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "That Claude session is not open on this PC.");
        if (!IsConnected(session))
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "That session is disconnected from Helm on the PC.", session.SessionId);
        if (WindowFor(session.Folder) is not { } window)
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "No VS Code window with Helm Bridge has that folder open.", session.SessionId);
        try
        {
            switch (command.Kind)
            {
                case WorkspaceCommandKind.SetModel:
                    await _sender.SendAsync(window, session.SessionId, "/model " + command.Text.Trim(), null, ct).ConfigureAwait(false);
                    break;
                case WorkspaceCommandKind.SetEffort:
                    await _sender.SendAsync(window, session.SessionId, "/effort " + command.Text.Trim(), null, ct).ConfigureAwait(false);
                    break;
                case WorkspaceCommandKind.SetMode:
                    var mode = PermissionModes.Normalize(command.Text.Trim());
                    var presses = PermissionModes.Presses(session.PermissionMode, mode, AutoModeAvailable);
                    if (presses < 0)
                        return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, $"Helm cannot switch from {PermissionModes.Label(session.PermissionMode)} to {PermissionModes.Label(mode)}; do it in VS Code.", session.SessionId);
                    if (presses > 0) await _sender.CycleModeAsync(window, session.SessionId, presses, ct).ConfigureAwait(false);
                    Registry.SetMode(session.SessionId, mode);
                    return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Received, null, session.SessionId);
                case WorkspaceCommandKind.CloseTab:
                    if (session.State is SessionState.Working or SessionState.NeedsPermission or SessionState.Question)
                        return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "Claude is still working in that tab; it was left open.", session.SessionId);
                    await window.RequestAsync("close", null, null, ct, ClaudeTabMatch.Label(session.Title)).ConfigureAwait(false);
                    return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Received, null, session.SessionId);
            }
        }
        catch (PromptSendException ex)
        {
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, ex.Message, session.SessionId);
        }
        catch (BridgeException ex)
        {
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, ex.Message, session.SessionId);
        }
        await Task.Delay(SlashSettle, ct).ConfigureAwait(false);
        return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Sent, null, session.SessionId);
    }

    /// <summary>Opens an earlier session of a window's folder as a Claude tab again.</summary>
    private async Task<WorkspaceCommandResult> ReopenAsync(WorkspaceCommand command, CancellationToken ct)
    {
        if (command.SessionId is not { } id || WindowFor(command.Folder) is not { } window)
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "No VS Code window with Helm Bridge has that folder open.", command.SessionId);
        var folder = window.Info.Folders.Where(f => IsUnder(command.Folder, f)).OrderByDescending(f => f.Length).First();
        if (Registry.Find(id) is null && History.TranscriptOf(folder, id) is null && History.TranscriptOf(command.Folder, id) is null)
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "Claude has no record of that session on this PC.", id);
        try
        {
            await window.RequestAsync("open", id, null, ct).ConfigureAwait(false);
        }
        catch (BridgeException ex)
        {
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, ex.Message, id);
        }
        if (!SeedSession(command.Folder, id)) SeedSession(folder, id);
        Changed?.Invoke(this, EventArgs.Empty);
        return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Received, null, id);
    }

    /// <summary>Auto mode is one more stop of Shift+Tab when the account has it (told by the relay from Claude's own list).</summary>
    public bool AutoModeAvailable { get; set; } = true;

    private void Forget(string commandId)
    {
        _receipts.TryRemove(commandId, out _);
        Registry.Forget(commandId);
    }

    private BridgeConnection? WindowFor(string folder)
    {
        var windows = (_bridge?.Windows ?? []).Where(w => Contains(w, folder)).ToList();
        // The window whose folder is the closest (deepest) match: a nested workspace beats its parent.
        return windows.OrderByDescending(w => w.Info.Folders.Where(f => IsUnder(folder, f)).Max(f => f.Length)).FirstOrDefault();
    }

    private static bool Contains(BridgeConnection window, string folder) => window.Info.Folders.Any(f => IsUnder(folder, f));

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    internal static bool IsUnder(string path, string root) => WorkspaceFolders.IsUnder(path, root);

    /// <summary>Whether the phone may see and steer <paramref name="session"/> (your choice on the PC).</summary>
    public bool IsConnected(AgentSession session) =>
        _settings.Current.IsConnected(session.SessionId, session.Folder, (_bridge?.Windows ?? []).SelectMany(w => w.Info.Folders));

    /// <summary>Connects or disconnects one session (null: back to its window's choice).</summary>
    public void SetSessionLink(string sessionId, bool? connected)
    {
        _settings.Update(s =>
        {
            if (connected is { } c) s.SessionLinks[sessionId] = c;
            else s.SessionLinks.Remove(sessionId);
            // Oldest choices go first: sessions are not reopened after days.
            var known = Registry.Sessions.Select(x => x.SessionId).ToHashSet();
            foreach (var stale in s.SessionLinks.Keys.Where(k => !known.Contains(k)).Take(Math.Max(0, s.SessionLinks.Count - WorkspaceSettings.MaxSessionLinks)).ToList())
                s.SessionLinks.Remove(stale);
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Connects or disconnects a whole VS Code window (its folder): its sessions follow unless set one by one.</summary>
    public void SetWindowLink(string folder, bool connected)
    {
        var sessions = Registry.Sessions.Where(x => IsUnder(x.Folder, folder)).Select(x => x.SessionId).ToList();
        _settings.Update(s =>
        {
            s.WindowLinks[SessionRegistry.FolderKey(folder)] = connected;
            // The window's switch is the newest choice for everything in it.
            foreach (var id in sessions) s.SessionLinks.Remove(id);
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionChanged(object? sender, SessionChange change)
    {
        var s = change.Session;
        if (s.TranscriptPath.Length > 0 && (s.Title.Length == 0 || change.StateChanged)) ReadTitleLater(s);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>How far back sessions are listed when a VS Code window connects.</summary>
    public static readonly TimeSpan SeedWindow = TimeSpan.FromHours(24);

    /// <summary>At most this many earlier sessions per folder.</summary>
    public const int SeedMax = 10;

    /// <summary>Lists the recent sessions of each newly connected window's folders (once per folder).</summary>
    private void SeedNewFolders()
    {
        var folders = (_bridge?.Windows ?? []).SelectMany(w => w.Info.Folders).Where(f => _seeded.TryAdd(SessionRegistry.FolderKey(f), 0)).ToList();
        if (folders.Count == 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var folder in folders) Seed(Registry, folder, _time.GetLocalNow() - SeedWindow, _projectsRoot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workspace: earlier sessions could not be read");
            }
        });
    }

    /// <summary>Adds the recent sessions Claude recorded for <paramref name="folder"/>, with their last prompt and answer.</summary>
    internal static int Seed(SessionRegistry registry, string folder, DateTimeOffset since, string? projectsRoot = null)
    {
        var count = 0;
        foreach (var (id, path, written) in ClaudeProjects.RecentSessions(folder, since, SeedMax, projectsRoot))
        {
            var tail = TranscriptReader.Read(path, maxTurns: 6);
            if (tail.Turns.Count == 0) continue;
            var lastUser = tail.Turns.LastOrDefault(t => t.FromUser);
            var lastClaude = tail.Turns.LastOrDefault(t => !t.FromUser);
            var answered = !tail.Turns[^1].FromUser;
            var seeded = registry.Seed(new AgentSession
            {
                MachineId = registry.MachineId,
                SessionId = id,
                Folder = folder,
                TranscriptPath = path,
                Title = tail.Title,
                // Finished with an answer: done. Ended on your prompt: it may still be working, or was stopped.
                State = answered ? SessionState.Done : SessionState.Idle,
                LastPrompt = lastUser?.Text,
                LastReply = lastClaude?.Text,
                StartedAt = written,
                UpdatedAt = written.ToLocalTime(),
                FromHistory = true,
            });
            if (seeded) count++;
        }
        return count;
    }

    private void ReadTitleLater(AgentSession session)
    {
        if (!_titleReads.TryAdd(session.SessionId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // Claude writes the title a moment after the first answer starts.
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var tail = TranscriptReader.Read(session.TranscriptPath, maxTurns: 0);
                if (tail.Title.Length > 0) Registry.SetTitle(session.SessionId, tail.Title);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Workspace: transcript title not read");
            }
            finally
            {
                _titleReads.TryRemove(session.SessionId, out _);
            }
        });
    }

    /// <summary>How often a held request checks whether you came back to the PC or it ran out of time.</summary>
    internal static readonly TimeSpan HoldCheck = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// One hook call. Most are only recorded. A PermissionRequest (a tool, or Claude's question) is held for the
    /// phone when <see cref="WorkspaceSettings.ApproveFromPhone"/> says so: the session shows it as pending, and the
    /// hook answers with the phone's decision. It gives up (no decision: VS Code shows its own dialog) when the time is
    /// up or, in "when away" mode, as soon as someone uses this PC.
    /// </summary>
    internal async Task<string?> HandleHookAsync(ClaudeHookEvent e)
    {
        Registry.Apply(e);
        if (e.Event != ClaudeHookEvent.PermissionRequest || !ShouldHold() || Registry.Find(e.SessionId) is not { } session || !IsConnected(session))
            return null;

        var hold = TimeSpan.FromMinutes(Math.Clamp(_settings.Current.HoldMinutes, 1, 25));
        var request = PendingRequest.From(e, Guid.NewGuid().ToString("N"), _time.GetLocalNow(), hold);
        var held = new Held(request, e.ToolInputJson);
        _held[request.RequestId] = held;
        Registry.SetPending(e.SessionId, request);
        _logger.LogInformation("Workspace: holding {Tool} in session {Session} for the phone", request.Tool, e.SessionId);
        var since = Environment.TickCount64;
        try
        {
            while (true)
            {
                var answered = await Task.WhenAny(held.Answer.Task, Task.Delay(HoldCheck)).ConfigureAwait(false);
                if (answered == held.Answer.Task) return await held.Answer.Task.ConfigureAwait(false);
                if (_time.GetLocalNow() >= request.ExpiresAt) return null;
                // Back at the PC: VS Code asks you there instead.
                if (_settings.Current.ApproveFromPhone == PhoneApprovals.WhenAway && RealInputTick() > since) return null;
            }
        }
        finally
        {
            _held.TryRemove(request.RequestId, out _);
            if (Registry.Find(e.SessionId)?.Pending?.RequestId == request.RequestId) Registry.SetPending(e.SessionId, null);
        }
    }

    /// <summary>The phone's answer to a held request.</summary>
    private WorkspaceCommandResult Answer(WorkspaceCommand command)
    {
        if (command.RequestId is not { } id || !_held.TryGetValue(id, out var held))
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "Claude is no longer waiting for that (answered in VS Code, or the time ran out).", command.SessionId);
        var json = held.Request.Kind switch
        {
            PendingKind.Question => PendingRequest.Answer(held.ToolInput, command.Answers ?? new Dictionary<string, string>()),
            PendingKind.Plan => PendingRequest.PlanDecision(command.Allow ? command.PlanMode ?? PermissionModes.Manual : null, command.Text),
            _ => PendingRequest.Decision(command.Allow, command.Text),
        };
        if (held.Request.Kind == PendingKind.Plan && command.Allow) Registry.SetMode(held.Request.SessionId, PermissionModes.Normalize(command.PlanMode));
        if (!held.Answer.TrySetResult(json))
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "That was answered already.", command.SessionId);
        _logger.LogInformation("Workspace: the phone answered {Tool} ({Answer})", held.Request.Tool,
            held.Request.Kind == PendingKind.Question ? "question" : command.Allow ? "allow" + (command.PlanMode is { } m ? " → " + m : "") : "deny");
        return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Received, null, held.Request.SessionId);
    }

    private bool ShouldHold() => PhoneLinked && _settings.Current.ApproveFromPhone switch
    {
        PhoneApprovals.Always => true,
        PhoneApprovals.WhenAway => Environment.TickCount64 - RealInputTick() >= TimeSpan.FromMinutes(Math.Max(1, _settings.Current.AwayAfterMinutes)).TotalMilliseconds,
        _ => false,
    };

    /// <summary>
    /// The last keyboard or mouse input that was not Helm typing into VS Code. Input during Helm's own typing is left
    /// out, so a prompt from the phone does not make the PC look used.
    /// </summary>
    private long RealInputTick()
    {
        if (_activity is null) return Interlocked.Read(ref _lastRealInput);
        var last = _activity.LastInputTick;
        if (last > _sender.TypedUntilTick) Interlocked.Exchange(ref _lastRealInput, last);
        return Interlocked.Read(ref _lastRealInput);
    }

    /// <summary>The pending requests held now (for the page and the tests).</summary>
    internal IReadOnlyList<PendingRequest> HeldRequests => _held.Values.Select(h => h.Request).ToList();

    private sealed record Held(PendingRequest Request, string? ToolInput)
    {
        public TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async ValueTask DisposeAsync()
    {
        // Every held request goes back to VS Code's own dialog.
        foreach (var held in _held.Values) held.Answer.TrySetResult(null);
        foreach (var waiting in _receipts.Values) waiting.TrySetCanceled();
        _receipts.Clear();
        if (_hooks is not null) await _hooks.DisposeAsync().ConfigureAwait(false);
        if (_bridge is not null) await _bridge.DisposeAsync().ConfigureAwait(false);
        _hooks = null;
        _bridge = null;
    }
}
