namespace Helm.Modules.Workspace;

/// <summary>Where a Claude session is, as far as its hooks tell.</summary>
public enum SessionState
{
    /// <summary>Started, nothing asked yet.</summary>
    Idle,
    /// <summary>Claude is working on a prompt.</summary>
    Working,
    /// <summary>Claude waits for you to allow a tool.</summary>
    NeedsPermission,
    /// <summary>Claude asked you a question (AskUserQuestion).</summary>
    Question,
    /// <summary>The turn is over; the answer is in <see cref="AgentSession.LastReply"/>.</summary>
    Done,
    /// <summary>The session closed.</summary>
    Ended,
}

/// <summary>One Claude Code session on one PC.</summary>
public sealed record AgentSession
{
    /// <summary>The PC (its <see cref="WorkspaceSettings.InstallId"/>): sessions of several PCs never mix.</summary>
    public required string MachineId { get; init; }
    public required string SessionId { get; init; }

    /// <summary>The session's working folder (its cwd), as Claude reports it.</summary>
    public required string Folder { get; init; }

    public string TranscriptPath { get; init; } = "";

    /// <summary>The title Claude gave the conversation (or the one you set); empty until it has one.</summary>
    public string Title { get; init; } = "";

    public SessionState State { get; init; }
    public string? LastPrompt { get; init; }
    public string? LastReply { get; init; }

    /// <summary>The tool waiting for permission, or the question being asked.</summary>
    public string? Waiting { get; init; }

    public string? PermissionMode { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>When the current (or last) prompt started, for "working for 3 min".</summary>
    public DateTimeOffset? WorkingSince { get; init; }

    public bool NeedsYou => State is SessionState.NeedsPermission or SessionState.Question;

    /// <summary>A permission or question the PC holds for the phone to answer; null when none.</summary>
    public PendingRequest? Pending { get; init; }

    /// <summary>
    /// Only known from Claude's records (listed when its window connected, or opened again from History); no hook of
    /// it arrived since Helm started. Such a session shows on the phone only while it is open in a Claude tab.
    /// </summary>
    public bool FromHistory { get; init; }

    /// <summary>Helm runs this session itself, in the background for the phone (it was outside VS Code).</summary>
    public bool Hosted { get; init; }
}

/// <summary>A session changed (<paramref name="Previous"/> is null for a new one). Raised on the hook's thread.</summary>
public sealed record SessionChange(AgentSession Session, AgentSession? Previous)
{
    /// <summary>The state changed, not only a detail (title, reply).</summary>
    public bool StateChanged => Previous is null || Previous.State != Session.State;
}

/// <summary>A prompt Helm typed into a session arrived there (its UserPromptSubmit hook carried the same text).</summary>
public sealed record PromptReceipt(string CommandId, string SessionId);

/// <summary>
/// The Claude sessions of this PC, kept up to date from Claude Code's hooks. Each hook moves a session through
/// <see cref="SessionState"/>; nothing here talks to Claude. Thread-safe.
/// </summary>
public sealed class SessionRegistry(string machineId, TimeProvider? time = null)
{
    /// <summary>Ended sessions stay listed this long, so the phone still shows how they finished.</summary>
    public static readonly TimeSpan KeepEnded = TimeSpan.FromHours(12);

    private readonly object _gate = new();
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<Expectation> _expected = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string MachineId { get; } = machineId;

    public event EventHandler<SessionChange>? Changed;
    public event EventHandler<PromptReceipt>? PromptReceived;

    public IReadOnlyList<AgentSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                Prune();
                return _sessions.Values.OrderByDescending(s => s.UpdatedAt).ToList();
            }
        }
    }

    public AgentSession? Find(string sessionId)
    {
        lock (_gate) return _sessions.GetValueOrDefault(sessionId);
    }

    /// <summary>Applies one hook call. Unknown events change nothing.</summary>
    public void Apply(ClaudeHookEvent e)
    {
        var now = _time.GetLocalNow();
        AgentSession? previous;
        AgentSession? next;
        PromptReceipt? receipt = null;
        lock (_gate)
        {
            previous = _sessions.GetValueOrDefault(e.SessionId);
            var current = previous ?? new AgentSession
            {
                MachineId = MachineId,
                SessionId = e.SessionId,
                Folder = e.Cwd,
                StartedAt = now,
                State = SessionState.Idle,
            };
            current = current with
            {
                Folder = e.Cwd.Length > 0 ? e.Cwd : current.Folder,
                TranscriptPath = e.TranscriptPath.Length > 0 ? e.TranscriptPath : current.TranscriptPath,
                PermissionMode = e.PermissionMode ?? current.PermissionMode,
                FromHistory = false,
            };
            next = Transition(current, e, now);
            if (next is null) return;
            next = next with { UpdatedAt = now };
            // Claude moved on (the tool ran, the turn ended): nothing waits for the phone any more.
            if (next.Pending is not null && e.Event is not ClaudeHookEvent.PermissionRequest and not ClaudeHookEvent.Notification)
                next = next with { Pending = null };
            _sessions[e.SessionId] = next;
            if (e.Event == ClaudeHookEvent.UserPromptSubmit && e.Prompt is { } prompt) receipt = MatchExpected(next, prompt, now);
        }
        Changed?.Invoke(this, new SessionChange(next, previous));
        if (receipt is not null) PromptReceived?.Invoke(this, receipt);
    }

    /// <summary>
    /// Lists a session found in Claude's records (one that ran before Helm started). A session Helm already knows
    /// from its hooks is newer and wins, so this never overwrites one.
    /// </summary>
    public bool Seed(AgentSession session)
    {
        var seeded = session with { MachineId = MachineId };
        lock (_gate)
        {
            if (_sessions.ContainsKey(session.SessionId)) return false;
            _sessions[session.SessionId] = seeded;
        }
        Changed?.Invoke(this, new SessionChange(seeded, null));
        return true;
    }

    /// <summary>Shows (or clears, with null) what the PC holds for the phone in this session.</summary>
    public void SetPending(string sessionId, PendingRequest? pending)
    {
        AgentSession? previous, next;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out previous) || previous.Pending == pending) return;
            next = previous with
            {
                Pending = pending,
                State = pending?.Kind == PendingKind.Question ? SessionState.Question : pending is not null ? SessionState.NeedsPermission : previous.State,
            };
            _sessions[sessionId] = next;
        }
        Changed?.Invoke(this, new SessionChange(next, previous));
    }

    /// <summary>The permission mode Helm just switched to (the next hook confirms or corrects it).</summary>
    public void SetMode(string sessionId, string mode)
    {
        AgentSession? previous, next;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out previous) || previous.PermissionMode == mode) return;
            next = previous with { PermissionMode = mode };
            _sessions[sessionId] = next;
        }
        Changed?.Invoke(this, new SessionChange(next, previous));
    }

    /// <summary>Sets the conversation title (read from the transcript), if it changed.</summary>
    public void SetTitle(string sessionId, string title) => Change(sessionId, s => s.Title == title ? s : s with { Title = title });

    /// <summary>The session's project folder turned out to be another one (a VS Code window with it connected).</summary>
    public void SetFolder(string sessionId, string folder) =>
        Change(sessionId, s => FolderKey(s.Folder) == FolderKey(folder) ? s : s with { Folder = folder });

    /// <summary>Helm started (or stopped) running this session in the background.</summary>
    public void SetHosted(string sessionId, bool hosted) => Change(sessionId, s => s.Hosted == hosted ? s : s with { Hosted = hosted });

    /// <summary>An ended session was opened again (its tab came back): it is listed as finished, not ended.</summary>
    public void Revive(string sessionId) =>
        Change(sessionId, s => s.State != SessionState.Ended ? s : s with { State = s.LastReply is not null ? SessionState.Done : SessionState.Idle, UpdatedAt = _time.GetLocalNow() });

    private void Change(string sessionId, Func<AgentSession, AgentSession> change)
    {
        AgentSession? previous, next;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out previous)) return;
            next = change(previous);
            if (ReferenceEquals(next, previous)) return;
            _sessions[sessionId] = next;
        }
        Changed?.Invoke(this, new SessionChange(next, previous));
    }

    /// <summary>
    /// Helm is about to type <paramref name="text"/> into a session: when that session's UserPromptSubmit carries the
    /// same text, <see cref="PromptReceived"/> reports <paramref name="commandId"/>. For a new session (no id yet) the
    /// first matching prompt in <paramref name="folder"/> counts.
    /// </summary>
    public void ExpectPrompt(string commandId, string? sessionId, string folder, string text, TimeSpan within)
    {
        lock (_gate) _expected.Add(new Expectation(commandId, sessionId, FolderKey(folder), Normalize(text), _time.GetLocalNow() + within));
    }

    /// <summary>Forgets an expectation (the command gave up).</summary>
    public void Forget(string commandId)
    {
        lock (_gate) _expected.RemoveAll(x => x.CommandId == commandId);
    }

    /// <summary>Folder paths as one key: Claude and VS Code differ in drive-letter case and trailing separators.</summary>
    public static string FolderKey(string path) =>
        WorkspaceFolders.Native(path).TrimEnd('\\').ToLowerInvariant();

    /// <summary>Prompt text as compared: line endings and outer whitespace do not count.</summary>
    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static AgentSession? Transition(AgentSession s, ClaudeHookEvent e, DateTimeOffset now) => e.Event switch
    {
        ClaudeHookEvent.SessionStart => s.State == SessionState.Ended ? s with { State = SessionState.Idle } : s,
        ClaudeHookEvent.UserPromptSubmit => s with
        {
            State = SessionState.Working,
            LastPrompt = e.Prompt ?? s.LastPrompt,
            WorkingSince = now,
            Waiting = null,
        },
        ClaudeHookEvent.PreToolUse when e.ToolName == "AskUserQuestion" => s with { State = SessionState.Question, Waiting = "A question for you" },
        // Any other tool call (or the answer to a question / permission) means Claude is back at work.
        ClaudeHookEvent.PreToolUse or ClaudeHookEvent.PostToolUse => s.State is SessionState.NeedsPermission or SessionState.Question or SessionState.Idle or SessionState.Done
            ? s with { State = SessionState.Working, Waiting = null, WorkingSince = s.WorkingSince ?? now }
            : s,
        ClaudeHookEvent.PermissionRequest when e.ToolName == "AskUserQuestion" => s with { State = SessionState.Question, Waiting = "A question for you" },
        ClaudeHookEvent.PermissionRequest when e.ToolName == "ExitPlanMode" => s with { State = SessionState.NeedsPermission, Waiting = "A plan for you to approve" },
        ClaudeHookEvent.PermissionRequest => s with { State = SessionState.NeedsPermission, Waiting = e.ToolName },
        // A question or a held request already says what Claude waits for; Claude Code's generic notice would blur it.
        ClaudeHookEvent.Notification when e.NotificationType == "permission_prompt" && s.State is not (SessionState.NeedsPermission or SessionState.Question) =>
            s with { State = SessionState.NeedsPermission, Waiting = e.Message },
        ClaudeHookEvent.Notification => s,
        // A Stop hook that made Claude continue: the turn goes on.
        ClaudeHookEvent.Stop when e.StopHookActive => s,
        ClaudeHookEvent.Stop => s with
        {
            State = SessionState.Done,
            LastReply = e.LastAssistantMessage ?? s.LastReply,
            Waiting = null,
        },
        ClaudeHookEvent.SessionEnd => s with { State = SessionState.Ended, Waiting = null },
        // A subagent started or finished: nothing changes here, but the phone's list of agents does.
        ClaudeHookEvent.SubagentStart or ClaudeHookEvent.SubagentStop => s,
        _ => null,
    };

    private PromptReceipt? MatchExpected(AgentSession session, string prompt, DateTimeOffset now)
    {
        _expected.RemoveAll(x => x.Expires < now);
        var text = Normalize(prompt);
        var folder = FolderKey(session.Folder);
        var match = _expected.FirstOrDefault(x => x.SessionId == session.SessionId && x.Text == text)
            ?? _expected.FirstOrDefault(x => x.SessionId is null && x.Folder == folder && x.Text == text);
        if (match is null) return null;
        _expected.Remove(match);
        return new PromptReceipt(match.CommandId, session.SessionId);
    }

    private void Prune()
    {
        var cutoff = _time.GetLocalNow() - KeepEnded;
        foreach (var id in _sessions.Where(p => p.Value.State == SessionState.Ended && p.Value.UpdatedAt < cutoff).Select(p => p.Key).ToList())
            _sessions.Remove(id);
    }

    private sealed record Expectation(string CommandId, string? SessionId, string Folder, string Text, DateTimeOffset Expires);
}
