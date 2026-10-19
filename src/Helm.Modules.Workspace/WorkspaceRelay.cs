using System.Collections.Concurrent;
using System.Threading.Channels;
using Helm.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>
/// The PC end of the phone link, over Helm Sync (end-to-end encrypted): publishes this host and its connected
/// sessions (<see cref="WorkspaceSync.Hosts"/>, <see cref="WorkspaceSync.Sessions"/>), the History of each window's
/// folder (<see cref="WorkspaceSync.History"/>), runs the commands the phone addressed to this PC
/// (<see cref="WorkspaceSync.Commands"/>) one at a time, and reports how each went (<see cref="WorkspaceSync.Results"/>).
/// While Claude works, a session is published again as its transcript grows (every tool call, every answer), so the
/// phone follows it live. Disconnected sessions never leave the PC.
/// </summary>
public sealed class WorkspaceRelay : IAsyncDisposable
{
    private static readonly TimeSpan PublishDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan Heartbeat = WorkspaceHostRecord.HeartbeatEvery;
    private static readonly TimeSpan LiveCheck = TimeSpan.FromSeconds(1);

    /// <summary>A working session with subagents is read again this often, as their own transcripts grow.</summary>
    private static readonly TimeSpan AgentsRefresh = TimeSpan.FromSeconds(3);

    private readonly WorkspaceEngine _engine;
    private readonly ISyncedCollection<WorkspaceHostRecord> _hosts;
    private readonly ISyncedCollection<WorkspaceSessionRecord> _sessions;
    private readonly ISyncedCollection<WorkspaceCommand> _commands;
    private readonly ISyncedCollection<WorkspaceResultRecord> _results;
    private readonly ISyncedCollection<WorkspaceHistoryRecord>? _history;
    private readonly string _machineName;
    private readonly Func<IReadOnlyList<SkillInfo>> _skills;
    private readonly Func<(IReadOnlyList<ModelInfo> Models, bool AutoMode)> _models;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _taken = new();
    private readonly Dictionary<string, CachedView> _views = new();
    private readonly Channel<WorkspaceCommand> _queue = Channel.CreateUnbounded<WorkspaceCommand>();
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _publishTimer;
    private readonly ITimer _heartbeat;
    private readonly ITimer _live;
    private readonly Task _worker;
    private int _scheduled;
    private DateTimeOffset _historyAt;

    public WorkspaceRelay(WorkspaceEngine engine, ISyncedCollection<WorkspaceHostRecord> hosts, ISyncedCollection<WorkspaceSessionRecord> sessions,
        ISyncedCollection<WorkspaceCommand> commands, ISyncedCollection<WorkspaceResultRecord> results, string machineName,
        Func<IReadOnlyList<SkillInfo>>? skills, ILogger logger, TimeProvider? time = null,
        Func<(IReadOnlyList<ModelInfo>, bool)>? models = null, ISyncedCollection<WorkspaceHistoryRecord>? history = null)
    {
        _engine = engine;
        _hosts = hosts;
        _sessions = sessions;
        _commands = commands;
        _results = results;
        _history = history;
        _machineName = machineName;
        _skills = skills ?? (() => []);
        _models = models ?? (() => ([], false));
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _publishTimer = _time.CreateTimer(_ => SafePublish(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _heartbeat = _time.CreateTimer(_ => { SafePublish(); SafeCleanup(); }, null, Heartbeat, Heartbeat);
        _live = _time.CreateTimer(_ => CheckLive(), null, LiveCheck, LiveCheck);
        _engine.Changed += OnEngineChanged;
        _commands.Changed += OnCommandsChanged;
        _worker = Task.Run(() => RunCommandsAsync(_stop.Token));
        SafePublish();
        TakeCommands();
    }

    /// <summary>The model aliases phones of 0.42 offer for a new session.</summary>
    public static IReadOnlyList<string> Models { get; } = ["opus", "sonnet", "haiku"];

    private void OnEngineChanged(object? sender, EventArgs e) => RequestPublish();

    /// <summary>
    /// Publishes soon. Changes close together become one write, and a steady stream of them (Claude calling tool after
    /// tool) still publishes every <see cref="PublishDelay"/>: the first request starts the wait, later ones join it.
    /// </summary>
    public void RequestPublish()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 0) _publishTimer.Change(PublishDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnCommandsChanged(object? sender, SyncedChangedEventArgs e) => TakeCommands();

    /// <summary>Writes this host and every connected session; deletes this PC's records that should no longer be seen.</summary>
    internal void Publish()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        lock (_gate)
        {
            var now = _time.GetLocalNow();
            var windows = _engine.Windows();
            var (models, autoMode) = _models();
            var host = new WorkspaceHostRecord
            {
                MachineId = _engine.MachineId,
                MachineName = _machineName,
                LastSeen = now,
                Running = true,
                Windows = windows.Select(w => new HostWindowInfo
                {
                    Folder = w.Folder,
                    Name = w.Name,
                    ClaudeTabs = w.Connection.Info.ClaudeTabs,
                    Connected = w.Sessions.Count == 0 || w.Sessions.Any(_engine.IsConnected),
                    OpenSessions = (w.OpenSessions ?? []).Where(id => w.Sessions.FirstOrDefault(s => s.SessionId == id) is { } s && _engine.IsConnected(s)).ToList(),
                    ActiveSession = w.ActiveSession,
                }).ToList(),
                Models = Models,
                ModelChoices = models,
                AutoModeAvailable = autoMode,
                Skills = _skills(),
                Activities = Activities(),
            };
            // LastSeen alone changes every time: write it only as the heartbeat, or with a real change.
            if (Interlocked.Exchange(ref _hostDue, 0) == 1 || _hosts.Get(_engine.MachineId) is not { } before || !before.Running || now - before.LastSeen > Heartbeat - TimeSpan.FromSeconds(5) ||
                !before.Windows.SequenceEqual(host.Windows) || !before.Skills.SequenceEqual(host.Skills) || before.MachineName != host.MachineName ||
                !before.ModelChoices.SequenceEqual(host.ModelChoices) || before.AutoModeAvailable != host.AutoModeAvailable ||
                !before.Activities.SequenceEqual(host.Activities))
                _hosts.Upsert(_engine.MachineId, host);

            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in windows)
            {
                foreach (var s in w.Sessions.Where(s => Shown(s, w) && _engine.IsConnected(s)))
                {
                    keep.Add(s.SessionId);
                    Write(s, w.Folder);
                }
            }
            foreach (var s in _engine.OtherSessions().Where(s => !s.FromHistory && _engine.IsConnected(s)))
            {
                keep.Add(s.SessionId);
                Write(s, "");
            }
            foreach (var item in _sessions.All().Where(i => i.Value.MachineId == _engine.MachineId && !keep.Contains(i.Id)).ToList())
            {
                _sessions.Delete(item.Id);
                _views.Remove(item.Id);
            }
            PublishHistory(windows, now);
        }
    }

    /// <summary>
    /// The phone shows a window's sessions open as Claude tabs, plus any that work or wait for you outside a tab (the
    /// side bar). A session only known from Claude's records shows only while a tab has it. A window whose bridge is
    /// too old to name its tabs shows everything it has.
    /// </summary>
    private static bool Shown(AgentSession s, WorkspaceWindow w) =>
        w.OpenSessions is not { } open
            ? true
            : open.Contains(s.SessionId) || !s.FromHistory && s.State is SessionState.Working or SessionState.NeedsPermission or SessionState.Question;

    private void Write(AgentSession s, string windowFolder)
    {
        var record = WorkspaceSync.ToRecord(s, windowFolder, View(s));
        if (_sessions.Get(s.SessionId) is { } old && WorkspaceSync.SameContent(old, record)) return;
        _sessions.Upsert(s.SessionId, record);
    }

    /// <summary>The History of each window's folder: once a minute at most, and only written when it changed.</summary>
    private void PublishHistory(IReadOnlyList<WorkspaceWindow> windows, DateTimeOffset now)
    {
        if (_history is null || now - _historyAt < Heartbeat && _historyAt != default && !_historyDue) return;
        _historyAt = now;
        _historyDue = false;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in windows.Where(w => w.Sessions.Count == 0 || w.Sessions.Any(_engine.IsConnected)).Select(w => w.Folder).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var id = WorkspaceSync.HistoryId(_engine.MachineId, folder);
            keep.Add(id);
            var entries = _engine.History.For(folder).Where(e => _engine.IsConnected(new AgentSession { MachineId = "", SessionId = e.SessionId, Folder = folder })).ToList();
            if (_history.Get(id) is { } old && old.Folder == folder && old.Sessions.SequenceEqual(entries)) continue;
            _history.Upsert(id, new WorkspaceHistoryRecord { MachineId = _engine.MachineId, Folder = folder, Sessions = entries, UpdatedAt = now });
        }
        foreach (var item in _history.All().Where(i => i.Value.MachineId == _engine.MachineId && !keep.Contains(i.Id)).ToList())
            _history.Delete(item.Id);
    }

    private volatile bool _historyDue = true;

    /// <summary>The History is read again at the next publish (a session ended, a tab was closed or reopened).</summary>
    public void RefreshHistory()
    {
        _historyDue = true;
        RequestPublish();
    }

    /// <summary>
    /// The timeline and details of a session, read from its transcript only when the file grew (or its state moved,
    /// or its subagents may have moved on): not on every publish.
    /// </summary>
    private TranscriptView? View(AgentSession s)
    {
        if (s.TranscriptPath.Length == 0) return null;
        var length = Length(s.TranscriptPath);
        var now = _time.GetLocalNow();
        if (_views.TryGetValue(s.SessionId, out var cached) && cached.Length == length && cached.State == s.State &&
            (cached.View.Agents.Count == 0 || now - cached.At < AgentsRefresh))
            return cached.View;
        var view = TranscriptReader.ReadView(s.TranscriptPath, WorkspaceSync.MaxItems);
        _views[s.SessionId] = new CachedView(length, s.State, now, view);
        return view;
    }

    /// <summary>Every second: a working session whose transcript grew is published again.</summary>
    private void CheckLive()
    {
        try
        {
            var due = false;
            lock (_gate)
            {
                var now = _time.GetLocalNow();
                foreach (var (id, cached) in _views)
                {
                    if (cached.State is not (SessionState.Working or SessionState.NeedsPermission or SessionState.Question)) continue;
                    if (_engine.Registry.Find(id) is not { } s) continue;
                    if (Length(s.TranscriptPath) != cached.Length || cached.View.Agents.Count > 0 && now - cached.At >= AgentsRefresh)
                    {
                        due = true;
                        break;
                    }
                }
            }
            if (due) RequestPublish();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Workspace relay: live check failed");
        }
    }

    private static long Length(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    private sealed record CachedView(long Length, SessionState State, DateTimeOffset At, TranscriptView View);

    /// <summary>1: write the host record at the next publish even if nothing changed (the phone asked whether this PC is there).</summary>
    private int _hostDue;

    /// <summary>The step of the command being run now, as the phone reads it.</summary>
    private volatile string? _doing;

    /// <summary>What this PC is busy with for the phone: the command at hand, requests it holds, sessions it runs.</summary>
    private IReadOnlyList<string> Activities()
    {
        var list = new List<string>();
        if (_doing is { } doing) list.Add(doing);
        var waiting = _queue.Reader.Count;
        if (waiting > 0) list.Add(waiting == 1 ? "1 more command from the phone waits its turn" : $"{waiting} more commands from the phone wait their turn");
        foreach (var held in _engine.HeldRequests) list.Add($"Holding Claude's request ({held.Tool}) for your answer");
        foreach (var run in _engine.HostedRuns)
        {
            var title = _engine.Registry.Find(run.SessionId)?.Title is { Length: > 0 } t ? t : Path.GetFileName(run.Folder);
            list.Add($"Running Claude in the background: {title}");
        }
        return list;
    }

    /// <summary>Queues the commands addressed to this PC that have no result yet.</summary>
    private void TakeCommands()
    {
        try
        {
            foreach (var item in _commands.All())
            {
                var c = item.Value;
                if (c.MachineId != _engine.MachineId || _results.Get(c.Id) is not null || !_taken.TryAdd(c.Id, 0)) continue;
                // The phone learns at once that this PC has it, even while another command is being typed.
                Report(new WorkspaceCommandResult(c.Id, WorkspaceCommandStatus.Sending, null, c.SessionId), c,
                    _doing is null ? "Helm on the PC got it" : "Helm on the PC got it; it waits for the command before it");
                _queue.Writer.TryWrite(c);
                RequestPublish();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace relay: commands could not be read");
        }
    }

    private async Task RunCommandsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var command in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    void Step(string text)
                    {
                        _doing = text;
                        Report(new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Sending, null, command.SessionId), command, text);
                        RequestPublish();
                    }
                    Step(command.Kind switch
                    {
                        WorkspaceCommandKind.NewSession => "Starting a new session",
                        WorkspaceCommandKind.Reopen => "Opening the session in VS Code",
                        WorkspaceCommandKind.Prompt => "Delivering your prompt",
                        _ => "Working on it",
                    });
                    WorkspaceCommandResult result;
                    try
                    {
                        result = await _engine.RunAsync(command, ct, Step).ConfigureAwait(false);
                    }
                    finally
                    {
                        _doing = null;
                        RequestPublish();
                    }
                    Report(result, command);
                    if (command.Kind == WorkspaceCommandKind.Ping)
                    {
                        Interlocked.Exchange(ref _hostDue, 1);
                        RequestPublish();
                    }
                    _logger.LogInformation("Workspace relay: command {Id} ({Kind}) from the phone → {Status}", command.Id, command.Kind, result.Status);
                    if (command.Kind is WorkspaceCommandKind.Reopen or WorkspaceCommandKind.CloseTab) RefreshHistory();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Workspace relay: command {Id} failed", command.Id);
                    Report(new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Failed, "Helm on the PC hit an error: " + ex.Message), command);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Report(WorkspaceCommandResult result, WorkspaceCommand command, string? step = null) =>
        _results.Upsert(command.Id, new WorkspaceResultRecord
        {
            CommandId = command.Id,
            MachineId = _engine.MachineId,
            Status = result.Status,
            Error = result.Error,
            SessionId = result.SessionId ?? command.SessionId,
            UpdatedAt = _time.GetLocalNow(),
            Step = step,
        });

    /// <summary>Removes this PC's old commands and results.</summary>
    internal void Cleanup()
    {
        var cutoff = _time.GetLocalNow() - WorkspaceSync.KeepCommands;
        foreach (var item in _commands.All().Where(i => i.Value.MachineId == _engine.MachineId && i.Value.CreatedAt < cutoff).ToList())
        {
            _commands.Delete(item.Id);
            _taken.TryRemove(item.Id, out _);
        }
        foreach (var item in _results.All().Where(i => i.Value.MachineId == _engine.MachineId && i.Value.UpdatedAt < cutoff).ToList())
            _results.Delete(item.Id);
    }

    private void SafePublish()
    {
        try
        {
            Publish();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace relay: publishing failed");
        }
    }

    private void SafeCleanup()
    {
        try
        {
            Cleanup();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace relay: cleanup failed");
        }
    }

    /// <summary>Stops, and tells the phone this PC is no longer reachable.</summary>
    public async ValueTask DisposeAsync()
    {
        _engine.Changed -= OnEngineChanged;
        _commands.Changed -= OnCommandsChanged;
        await _publishTimer.DisposeAsync().ConfigureAwait(false);
        await _heartbeat.DisposeAsync().ConfigureAwait(false);
        await _live.DisposeAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        try
        {
            lock (_gate)
            {
                if (_hosts.Get(_engine.MachineId) is { } host)
                    _hosts.Upsert(_engine.MachineId, host with { Running = false, LastSeen = _time.GetLocalNow() });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace relay: could not mark this PC as off");
        }
        _stop.Dispose();
    }
}
