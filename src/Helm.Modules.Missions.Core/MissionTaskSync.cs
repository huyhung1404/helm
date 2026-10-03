using Helm.Core.Settings;
using Helm.Core.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.Missions;

/// <summary>
/// Sends the current step to another tool's to-do list (Tracker, through <see cref="ITaskBridge"/>) and keeps the two in
/// step: the task done there completes the step here, and a step completed here (on any device) completes its task.
/// Runs while it lives (the platform modules hold it from the start). Both tools must be on.
/// </summary>
public sealed class MissionTaskSync
{
    private readonly MissionsStore _store;
    private readonly Func<IEnumerable<ITaskBridge>> _bridges;
    private readonly ISettingsStoreFactory _settings;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly HashSet<ITaskBridge> _watched = [];
    private int _syncing;

    public MissionTaskSync(MissionsStore store, Func<IEnumerable<ITaskBridge>> bridges, ISettingsStoreFactory settings, ILogger<MissionTaskSync>? logger = null)
    {
        _store = store;
        _bridges = bridges;
        _settings = settings;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _store.Changed += (_, _) => Sync();
    }

    /// <summary>The to-do list to send steps to while both tools are on; null otherwise.</summary>
    public ITaskBridge? Bridge
    {
        get
        {
            try
            {
                var enabled = _settings.Get<GeneralSettings>(GeneralSettings.StoreId).Current.EnabledModules;
                bool On(string id) => !enabled.TryGetValue(id, out var on) || on;
                if (!On(MissionsIds.ModuleId)) return null;
                var bridge = _bridges().FirstOrDefault(b => On(b.ModuleId));
                if (bridge is not null) Watch(bridge);
                return bridge;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Looking for a to-do list failed");
                return null;
            }
        }
    }

    /// <summary>The task of a step while it is still there (done or not); null when never sent or deleted.</summary>
    public bool? TaskState(MissionStep step) => step.TaskId is { } id ? Bridge?.IsDone(id) : null;

    /// <summary>
    /// Sends the current step of a running mission as a task due on the day the step should be done; returns the
    /// tool's name, or null when there is nothing to send or no to-do list.
    /// </summary>
    public string? Send(string missionId, TimeZoneInfo zone)
    {
        if (Bridge is not { } bridge || _store.GetMission(missionId) is not { Status: MissionStatus.Active } mission) return null;
        if (_store.CurrentStep(missionId) is not { } current) return null;
        if (current.Value.TaskId is { } existing && bridge.IsDone(existing) is not null) return bridge.Name;
        var step = current.Value;
        var today = MissionPace.Day(_store.Now, zone);
        var since = step.StartedAt ?? _store.Steps(missionId).LastOrDefault(s => s.Value.IsDone)?.Value.CompletedAt ?? mission.StartedAt ?? _store.Now;
        var left = Math.Max(0, step.EstimateDays - (_store.Now - since).TotalDays);
        var notes = $"Mission: {mission.Title}" + (step.DoneWhen.Length > 0 ? $"\nDone when: {step.DoneWhen}" : "")
            + (step.Description.Length > 0 ? $"\n{step.Description}" : "");
        if (bridge.Add(step.Title, notes, today.AddDays((int)Math.Ceiling(left))) is not { } taskId) return null;
        _store.SetTask(current.Id, taskId);
        return bridge.Name;
    }

    /// <summary>Brings steps and tasks together after a change on either side (also runs by itself on changes).</summary>
    public void Sync()
    {
        // A completion here completes a task there, which raises a change here again: one pass at a time.
        if (Interlocked.Exchange(ref _syncing, 1) == 1) return;
        try
        {
            if (Bridge is not { } bridge) return;
            foreach (var m in _store.Missions().Where(m => m.Value.Status is MissionStatus.Active or MissionStatus.Completed))
            {
                foreach (var s in _store.Steps(m.Id).Where(s => s.Value.TaskId is not null))
                {
                    var done = bridge.IsDone(s.Value.TaskId!);
                    if (s.Value.IsDone && done == false) bridge.Complete(s.Value.TaskId!);
                    else if (!s.Value.IsDone && done == true && m.Value.Status == MissionStatus.Active
                             && _store.CurrentStep(m.Id)?.Id == s.Id)
                        _store.Complete(m.Id, s.Value.Note.Length > 0 ? s.Value.Note : $"Done in {bridge.Name}.");
                }
            }
        }
        catch (Exception ex)
        {
            // Runs from change events, possibly on a sync thread: never let it escape.
            _logger.LogWarning(ex, "Syncing missions with the to-do list failed");
        }
        finally
        {
            Interlocked.Exchange(ref _syncing, 0);
        }
    }

    private void Watch(ITaskBridge bridge)
    {
        lock (_gate)
        {
            if (_watched.Add(bridge)) bridge.Changed += (_, _) => Sync();
        }
    }
}
