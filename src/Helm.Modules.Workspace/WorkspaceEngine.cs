using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>A VS Code window and the Claude sessions whose folder is in it.</summary>
public sealed record WorkspaceWindow(BridgeConnection Connection, IReadOnlyList<AgentSession> Sessions)
{
    public string Name => Connection.Info.Name;
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
    private readonly IUserNotifications? _notifications;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _receipts = new();
    private readonly ConcurrentDictionary<string, byte> _titleReads = new();
    private HookListener? _hooks;
    private BridgeHost? _bridge;

    private WorkspaceEngine(ISettingsStore<WorkspaceSettings> settings, SessionRegistry registry, PromptSender sender,
        IUserNotifications? notifications, ILogger logger, TimeProvider time)
    {
        _settings = settings;
        Registry = registry;
        _sender = sender;
        _notifications = notifications;
        _logger = logger;
        _time = time;
        registry.Changed += OnSessionChanged;
        registry.PromptReceived += (_, r) =>
        {
            if (_receipts.TryRemove(r.CommandId, out var waiting)) waiting.TrySetResult(r.SessionId);
        };
    }

    public SessionRegistry Registry { get; }

    public string MachineId => Registry.MachineId;

    public int HookPort => _hooks?.Port ?? 0;

    public string? BridgePipe => _bridge?.PipeName;

    /// <summary>Sessions, windows or their state changed. Raised off the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Starts listening. A taken hook port is replaced by a free one (and Claude's settings follow).</summary>
    public static WorkspaceEngine Start(ISettingsStore<WorkspaceSettings> settings, HelmPaths paths, PromptSender sender,
        IUserNotifications? notifications, ILogger logger, TimeProvider? time = null, string? bridgePipe = null)
    {
        EnsureIdentity(settings);
        var s = settings.Current;
        var engine = new WorkspaceEngine(settings, new SessionRegistry(s.InstallId, time), sender, notifications, logger, time ?? TimeProvider.System);
        try
        {
            engine._hooks = HookListener.Start(s.HookPort, s.HookToken, engine.Registry.Apply, logger);
        }
        catch (SocketException ex) when (s.HookPort != 0)
        {
            logger.LogWarning(ex, "Workspace: hook port {Port} is taken, picking another", s.HookPort);
            engine._hooks = HookListener.Start(0, s.HookToken, engine.Registry.Apply, logger);
        }
        if (engine._hooks.Port != s.HookPort) settings.Update(x => x.HookPort = engine._hooks.Port);
        engine._bridge = new BridgeHost(bridgePipe ?? WorkspaceEndpoint.PipeName(paths), logger);
        engine._bridge.WindowsChanged += (_, _) => engine.Changed?.Invoke(engine, EventArgs.Empty);
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

    /// <summary>The connected VS Code windows, each with its sessions (a session belongs to the window that has its folder).</summary>
    public IReadOnlyList<WorkspaceWindow> Windows()
    {
        var sessions = Registry.Sessions;
        return (_bridge?.Windows ?? [])
            .OrderBy(w => w.Info.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(w => new WorkspaceWindow(w, sessions.Where(s => Contains(w, s.Folder)).ToList()))
            .ToList();
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
        if (string.IsNullOrWhiteSpace(command.Text))
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "The prompt is empty.");

        BridgeConnection? window;
        string? sessionId = null;
        string folder;
        if (command.Kind == WorkspaceCommandKind.Prompt)
        {
            if (command.SessionId is null || Registry.Find(command.SessionId) is not { } session)
                return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "That Claude session is not open on this PC.");
            if (session.State == SessionState.Ended)
                return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "That Claude session has ended.");
            sessionId = session.SessionId;
            folder = session.Folder;
            window = WindowFor(folder);
        }
        else
        {
            folder = command.Folder;
            window = WindowFor(folder);
        }
        if (window is null)
            return new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "No VS Code window with Helm Bridge has that folder open.");

        var receipt = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _receipts[command.Id] = receipt;
        Registry.ExpectPrompt(command.Id, sessionId, folder, command.Text, ReceiptTimeout + TimeSpan.FromSeconds(10));
        try
        {
            await _sender.SendAsync(window, sessionId, command.Text, command.Kind == WorkspaceCommandKind.NewSession ? command.Model : null, ct).ConfigureAwait(false);
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
    internal static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        var p = SessionRegistry.FolderKey(path);
        var r = SessionRegistry.FolderKey(root);
        return p == r || p.StartsWith(r + "\\", StringComparison.Ordinal);
    }

    private void OnSessionChanged(object? sender, SessionChange change)
    {
        var s = change.Session;
        if (s.TranscriptPath.Length > 0 && (s.Title.Length == 0 || change.StateChanged)) ReadTitleLater(s);
        if (change.StateChanged && change.Previous is not null) Notify(s);
        Changed?.Invoke(this, EventArgs.Empty);
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

    private void Notify(AgentSession s)
    {
        if (!_settings.Current.NotifyOnPc || _notifications is null) return;
        var name = s.Title.Length > 0 ? s.Title : Path.GetFileName(s.Folder.TrimEnd('\\', '/'));
        var text = s.State switch
        {
            SessionState.Done => Shorten(s.LastReply) ?? "Claude finished.",
            SessionState.NeedsPermission => "Claude needs your permission" + (s.Waiting is { } tool ? $" for {tool}." : "."),
            SessionState.Question => "Claude asked you a question.",
            _ => null,
        };
        if (text is not null) _notifications.Show(name, text);
    }

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ReplaceLineEndings(" ").Trim();
        return t.Length <= 180 ? t : t[..177] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var waiting in _receipts.Values) waiting.TrySetCanceled();
        _receipts.Clear();
        if (_hooks is not null) await _hooks.DisposeAsync().ConfigureAwait(false);
        if (_bridge is not null) await _bridge.DisposeAsync().ConfigureAwait(false);
        _hooks = null;
        _bridge = null;
    }
}
