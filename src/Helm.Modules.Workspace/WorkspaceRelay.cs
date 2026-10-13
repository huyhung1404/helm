using System.Collections.Concurrent;
using System.Threading.Channels;
using Helm.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>
/// The PC end of the phone link, over Helm Sync (end-to-end encrypted): publishes this host and its connected
/// sessions (<see cref="WorkspaceSync.Hosts"/>, <see cref="WorkspaceSync.Sessions"/>), runs the commands the phone
/// addressed to this PC (<see cref="WorkspaceSync.Commands"/>) one at a time, and reports how each went
/// (<see cref="WorkspaceSync.Results"/>). Disconnected sessions never leave the PC.
/// </summary>
public sealed class WorkspaceRelay : IAsyncDisposable
{
    private static readonly TimeSpan PublishDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(1);

    private readonly WorkspaceEngine _engine;
    private readonly ISyncedCollection<WorkspaceHostRecord> _hosts;
    private readonly ISyncedCollection<WorkspaceSessionRecord> _sessions;
    private readonly ISyncedCollection<WorkspaceCommand> _commands;
    private readonly ISyncedCollection<WorkspaceResultRecord> _results;
    private readonly string _machineName;
    private readonly Func<IReadOnlyList<SkillInfo>> _skills;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _taken = new();
    private readonly Dictionary<string, (string Key, IReadOnlyList<TranscriptTurn> Turns)> _turns = new();
    private readonly Channel<WorkspaceCommand> _queue = Channel.CreateUnbounded<WorkspaceCommand>();
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _publishTimer;
    private readonly ITimer _heartbeat;
    private readonly Task _worker;

    public WorkspaceRelay(WorkspaceEngine engine, ISyncedCollection<WorkspaceHostRecord> hosts, ISyncedCollection<WorkspaceSessionRecord> sessions,
        ISyncedCollection<WorkspaceCommand> commands, ISyncedCollection<WorkspaceResultRecord> results, string machineName,
        Func<IReadOnlyList<SkillInfo>>? skills, ILogger logger, TimeProvider? time = null)
    {
        _engine = engine;
        _hosts = hosts;
        _sessions = sessions;
        _commands = commands;
        _results = results;
        _machineName = machineName;
        _skills = skills ?? (() => []);
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _publishTimer = _time.CreateTimer(_ => SafePublish(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _heartbeat = _time.CreateTimer(_ => { SafePublish(); SafeCleanup(); }, null, Heartbeat, Heartbeat);
        _engine.Changed += OnEngineChanged;
        _commands.Changed += OnCommandsChanged;
        _worker = Task.Run(() => RunCommandsAsync(_stop.Token));
        SafePublish();
        TakeCommands();
    }

    /// <summary>The model aliases the phone offers for a new session.</summary>
    public static IReadOnlyList<string> Models { get; } = ["opus", "sonnet", "haiku"];

    private void OnEngineChanged(object? sender, EventArgs e) => RequestPublish();

    /// <summary>Publishes soon (changes close together become one write).</summary>
    public void RequestPublish() => _publishTimer.Change(PublishDelay, Timeout.InfiniteTimeSpan);

    private void OnCommandsChanged(object? sender, SyncedChangedEventArgs e) => TakeCommands();

    /// <summary>Writes this host and every connected session; deletes this PC's records that should no longer be seen.</summary>
    internal void Publish()
    {
        lock (_gate)
        {
            var now = _time.GetLocalNow();
            var windows = _engine.Windows();
            var host = new WorkspaceHostRecord
            {
                MachineId = _engine.MachineId,
                MachineName = _machineName,
                LastSeen = now,
                Running = true,
                Windows = windows.Select(w => new HostWindowInfo
                {
                    Folder = w.Connection.Info.Folders.FirstOrDefault() ?? "",
                    Name = w.Name,
                    ClaudeTabs = w.Connection.Info.ClaudeTabs,
                    Connected = w.Sessions.Count == 0 || w.Sessions.Any(_engine.IsConnected),
                }).ToList(),
                Models = Models,
                Skills = _skills(),
            };
            // LastSeen alone changes every time: write it only as the heartbeat, or with a real change.
            if (_hosts.Get(_engine.MachineId) is not { } before || !before.Running || now - before.LastSeen > Heartbeat - TimeSpan.FromSeconds(10) ||
                !before.Windows.SequenceEqual(host.Windows) || !before.Skills.SequenceEqual(host.Skills) || before.MachineName != host.MachineName)
                _hosts.Upsert(_engine.MachineId, host);

            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in windows)
            {
                var folder = w.Connection.Info.Folders.FirstOrDefault() ?? "";
                foreach (var s in w.Sessions.Where(_engine.IsConnected))
                {
                    keep.Add(s.SessionId);
                    var record = WorkspaceSync.ToRecord(s, folder, Turns(s));
                    if (_sessions.Get(s.SessionId) is { } old && WorkspaceSync.SameContent(old, record)) continue;
                    _sessions.Upsert(s.SessionId, record);
                }
            }
            foreach (var s in _engine.OtherSessions().Where(_engine.IsConnected))
            {
                keep.Add(s.SessionId);
                var record = WorkspaceSync.ToRecord(s, "", Turns(s));
                if (_sessions.Get(s.SessionId) is { } old && WorkspaceSync.SameContent(old, record)) continue;
                _sessions.Upsert(s.SessionId, record);
            }
            foreach (var item in _sessions.All().Where(i => i.Value.MachineId == _engine.MachineId && !keep.Contains(i.Id)).ToList())
            {
                _sessions.Delete(item.Id);
                _turns.Remove(item.Id);
            }
        }
    }

    /// <summary>The last messages, read from the transcript only when the session moved on (not on every tool call).</summary>
    private IReadOnlyList<TranscriptTurn> Turns(AgentSession s)
    {
        var key = $"{s.State}|{s.LastPrompt?.Length}|{s.LastReply?.Length}|{s.TranscriptPath}";
        if (_turns.TryGetValue(s.SessionId, out var cached) && cached.Key == key) return cached.Turns;
        var turns = s.TranscriptPath.Length == 0 ? [] : TranscriptReader.Read(s.TranscriptPath, WorkspaceSync.MaxTurns).Turns;
        _turns[s.SessionId] = (key, turns);
        return turns;
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
                _queue.Writer.TryWrite(c);
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
                    Report(new WorkspaceCommandResult(command.Id, WorkspaceCommandStatus.Sending, null, command.SessionId), command);
                    var result = await _engine.RunAsync(command, ct).ConfigureAwait(false);
                    Report(result, command);
                    _logger.LogInformation("Workspace relay: command {Id} from the phone → {Status}", command.Id, result.Status);
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

    private void Report(WorkspaceCommandResult result, WorkspaceCommand command) =>
        _results.Upsert(command.Id, new WorkspaceResultRecord
        {
            CommandId = command.Id,
            MachineId = _engine.MachineId,
            Status = result.Status,
            Error = result.Error,
            SessionId = result.SessionId ?? command.SessionId,
            UpdatedAt = _time.GetLocalNow(),
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
