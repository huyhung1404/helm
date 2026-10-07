using Helm.Core.Sync;

namespace Helm.Modules.Missions;

/// <summary>Sizes a mission may have (the import, AI agents and the editors all keep to them).</summary>
public static class MissionLimits
{
    public const int Phases = 30;
    public const int Steps = 300;
    public const int ChecklistItems = 20;
    public const int Resources = 10;
    public const int Title = 200;
    public const int Goal = 1000;
    public const int Note = 2000;
    public const int Reward = 300;
    public const int Description = 4000;
    public const int DoneWhen = 500;
    public const int ChecklistText = 200;
    public const int ResourceText = 500;
    public const int ResourceLabel = 40;
    public const int ResourceTitle = 200;
    public const int ResourceUrl = 1000;
    public const int ResourceBody = 4000;
    public const int ResourceColumns = 8;
    public const int ColumnName = 40;
    public const int ResourceRows = 60;
    public const int Cell = 300;

    /// <summary>Rows in all the resources of one step, so a step record stays far below the sync server's 1 MB.</summary>
    public const int StepResourceRows = 150;

    /// <summary>Characters of resource texts in one step, for the same reason.</summary>
    public const int StepResourceBody = 12000;

    public const double MinEstimateDays = 0.25;
    public const double MaxEstimateDays = 60;

    public static string Clip(string? text, int max)
    {
        var s = (text ?? "").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd();
    }
}

/// <summary>
/// Missions, their steps and the history on top of Helm Sync. Steps are done strictly in order: only the current step
/// (the first one not done) of an active mission can be started, ticked, completed or skipped. Every write is local and
/// immediate (the sync engine uploads it in the background). Callable from any thread; <see cref="Changed"/> may be
/// raised on a background thread after a sync.
/// </summary>
public sealed class MissionsStore
{
    public const string MissionsCollection = "missions.missions";
    public const string StepsCollection = "missions.steps";
    public const string HistoryCollection = "missions.history";

    private readonly ISyncedCollection<Mission> _missions;
    private readonly ISyncedCollection<MissionStep> _steps;
    private readonly ISyncedLog<MissionEvent> _history;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public MissionsStore(ISyncedCollection<Mission> missions, ISyncedCollection<MissionStep> steps, ISyncedLog<MissionEvent> history, TimeProvider? time = null)
    {
        _missions = missions;
        _steps = steps;
        _history = history;
        _time = time ?? TimeProvider.System;
        _missions.Changed += OnChanged;
        _steps.Changed += OnChanged;
        _history.Changed += OnChanged;
    }

    /// <summary>Raised after any local write or synced change (on the writing thread or a thread-pool thread).</summary>
    public event EventHandler? Changed;

    public DateTimeOffset Now => _time.GetUtcNow();

    private void OnChanged(object? sender, SyncedChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    // ---- Reading -------------------------------------------------------------------------------------------------

    /// <summary>Missions in picker order.</summary>
    public IReadOnlyList<SyncedItem<Mission>> Missions() =>
        _missions.All().OrderBy(m => m.Value.Order).ThenBy(m => m.Value.CreatedAt).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();

    public Mission? GetMission(string id) => _missions.Get(id);

    public MissionStep? GetStep(string id) => _steps.Get(id);

    /// <summary>A mission's steps in order. Steps that arrived before their mission (sync order) are listed too; the pages show missions only.</summary>
    public IReadOnlyList<SyncedItem<MissionStep>> Steps(string missionId) =>
        _steps.All().Where(s => s.Value.MissionId == missionId)
            .OrderBy(s => s.Value.Order).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();

    /// <summary>The first step not done, or null when every step is done.</summary>
    public SyncedItem<MissionStep>? CurrentStep(string missionId) => Current(Steps(missionId));

    private static SyncedItem<MissionStep>? Current(IReadOnlyList<SyncedItem<MissionStep>> steps) => steps.FirstOrDefault(s => !s.Value.IsDone);

    /// <summary>The history, oldest first (one mission, or all of them).</summary>
    public IReadOnlyList<MissionEvent> History(string? missionId = null) =>
        _history.All().Select(e => e.Value).Where(e => missionId is null || e.MissionId == missionId).OrderBy(e => e.At).ToList();

    /// <summary>Finds a mission by id or title (case does not matter); null when none or several match.</summary>
    public SyncedItem<Mission>? Find(string idOrTitle)
    {
        var all = Missions();
        if (all.FirstOrDefault(m => m.Id == idOrTitle) is { } byId) return byId;
        var matches = all.Where(m => string.Equals(m.Value.Title.Trim(), idOrTitle.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    // ---- Creating ------------------------------------------------------------------------------------------------

    /// <summary>Creates a <see cref="MissionStatus.Planned"/> mission with all its steps; returns its id.</summary>
    public string Create(MissionDraft draft, MissionSource source)
    {
        var phases = draft.Phases.Where(p => p.Steps.Count > 0).ToList();
        if (MissionLimits.Clip(draft.Title, MissionLimits.Title).Length == 0) throw new ArgumentException("A mission needs a title.", nameof(draft));
        if (phases.Count == 0) throw new ArgumentException("A mission needs at least one step.", nameof(draft));
        lock (_gate)
        {
            var now = Now;
            var existing = _missions.All();
            var keys = phases.Select(_ => NewKey()).ToList();
            var mission = new Mission
            {
                Title = MissionLimits.Clip(draft.Title, MissionLimits.Title),
                Goal = MissionLimits.Clip(draft.Goal, MissionLimits.Goal),
                Note = MissionLimits.Clip(draft.Note, MissionLimits.Note),
                Reward = MissionLimits.Clip(draft.Reward, MissionLimits.Reward),
                Deadline = draft.Deadline,
                Phases = phases.Select((p, i) => new MissionPhase
                {
                    Key = keys[i],
                    Title = MissionLimits.Clip(p.Title, MissionLimits.Title) is { Length: > 0 } t ? t : $"Phase {i + 1}",
                    Reward = MissionLimits.Clip(p.Reward, MissionLimits.Reward),
                }).ToList(),
                Status = MissionStatus.Planned,
                Source = source,
                Order = existing.Count == 0 ? 0 : existing.Max(m => m.Value.Order) + 1,
                CreatedAt = now,
            };
            var id = _missions.Add(mission);
            var order = 0;
            for (var i = 0; i < phases.Count; i++)
                foreach (var step in phases[i].Steps)
                    _steps.Add(NewStep(id, keys[i], order++, step));
            Log(MissionEventKind.MissionCreated, id, mission, null, null);
            return id;
        }
    }

    private static MissionStep NewStep(string missionId, string phaseKey, double order, StepDraft step)
    {
        var resources = MissionResources.Clean(step.Resources);
        return new()
        {
            MissionId = missionId,
            PhaseKey = phaseKey,
            Order = order,
            Title = MissionLimits.Clip(step.Title, MissionLimits.Title),
            Description = MissionLimits.Clip(step.Description, MissionLimits.Description),
            DoneWhen = MissionLimits.Clip(step.DoneWhen, MissionLimits.DoneWhen),
            EstimateDays = ClampEstimate(step.EstimateDays),
            Checklist = (step.Checklist ?? []).Select(c => MissionLimits.Clip(c, MissionLimits.ChecklistText)).Where(c => c.Length > 0)
                .Take(MissionLimits.ChecklistItems).Select(c => new ChecklistItem { Text = c }).ToList(),
            Resources = resources.Select(MissionResources.Line).ToList(),
            // Plain lines say it all: only details need the record older versions do not know.
            Materials = resources.Any(MissionResources.HasDetails) ? resources : [],
        };
    }

    public static double ClampEstimate(double days) =>
        double.IsFinite(days) ? Math.Clamp(Math.Round(days * 4) / 4, MissionLimits.MinEstimateDays, MissionLimits.MaxEstimateDays) : 1;

    private static string NewKey() => Guid.NewGuid().ToString("N")[..8];

    // ---- Running -------------------------------------------------------------------------------------------------

    /// <summary>Planned → Active: the pace counts from now.</summary>
    public bool Start(string missionId)
    {
        lock (_gate)
        {
            if (_missions.Get(missionId) is not { Status: MissionStatus.Planned } m) return false;
            var started = m with { Status = MissionStatus.Active, StartedAt = Now };
            _missions.Upsert(missionId, started);
            Log(MissionEventKind.MissionStarted, missionId, started, null, null);
            return true;
        }
    }

    /// <summary>Logs that work on the current step began (once).</summary>
    public bool StartStep(string missionId)
    {
        lock (_gate)
        {
            if (!TryCurrent(missionId, out var mission, out var current) || current.Value.StartedExplicitly) return false;
            var step = current.Value with { StartedAt = Now, StartedExplicitly = true };
            _steps.Upsert(current.Id, step);
            Log(MissionEventKind.StepStarted, missionId, mission, current.Id, step);
            return true;
        }
    }

    /// <summary>Ticks or unticks a checklist item of the current step.</summary>
    public bool ToggleChecklist(string missionId, int index)
    {
        lock (_gate)
        {
            if (!TryCurrent(missionId, out var mission, out var current)) return false;
            var list = current.Value.Checklist.ToList();
            if (index < 0 || index >= list.Count) return false;
            var ticked = !list[index].IsDone;
            list[index] = list[index] with { DoneAt = ticked ? Now : null };
            var step = current.Value with { Checklist = list };
            _steps.Upsert(current.Id, step);
            if (ticked) Log(MissionEventKind.ChecklistTicked, missionId, mission, current.Id, step, list[index].Text);
            return true;
        }
    }

    /// <summary>Completes the current step (with an optional note); null when there is no current step to complete.</summary>
    public StepOutcome? Complete(string missionId, string? note = null) => Finish(missionId, note, skipped: false);

    /// <summary>Skips the current step (it counts as passed, marked skipped); null when there is none.</summary>
    public StepOutcome? Skip(string missionId, string? note = null) => Finish(missionId, note, skipped: true);

    private StepOutcome? Finish(string missionId, string? note, bool skipped)
    {
        lock (_gate)
        {
            if (!TryCurrent(missionId, out var mission, out var current)) return null;
            var steps = Steps(missionId);
            var index = IndexOf(steps, current.Id);
            var now = Now;
            var started = current.Value.StartedAt
                ?? (index > 0 ? steps[index - 1].Value.CompletedAt : null)
                ?? mission.StartedAt
                ?? now;
            if (started > now) started = now;
            var step = current.Value with
            {
                StartedAt = started,
                CompletedAt = now,
                Skipped = skipped,
                Note = note is null ? current.Value.Note : MissionLimits.Clip(note, MissionLimits.Note),
            };
            _steps.Upsert(current.Id, step);
            Log(skipped ? MissionEventKind.StepSkipped : MissionEventKind.StepCompleted, missionId, mission, current.Id, step, step.Note);

            var after = steps.Select(s => s.Id == current.Id ? new SyncedItem<MissionStep>(s.Id, step, s.UpdatedAt) : s).ToList();
            MissionPhase? phaseDone = null;
            if (after.Where(s => s.Value.PhaseKey == step.PhaseKey).All(s => s.Value.IsDone))
            {
                phaseDone = mission.Phase(step.PhaseKey);
                Log(MissionEventKind.PhaseCompleted, missionId, mission, current.Id, step, phaseDone?.Title ?? "");
            }
            var next = Current(after);
            var missionDone = next is null;
            if (missionDone)
            {
                var finished = mission with { Status = MissionStatus.Completed, CompletedAt = now };
                _missions.Upsert(missionId, finished);
                Log(MissionEventKind.MissionCompleted, missionId, finished, null, null);
            }
            return new StepOutcome(current.Id, step.Title, after.Count(s => s.Value.IsDone), after.Count, phaseDone, missionDone, next?.Value.Title);
        }
    }

    /// <summary>
    /// Opens the most recent done or skipped step again; it becomes the current step. A finished mission becomes
    /// active again.
    /// </summary>
    public bool ReopenLast(string missionId)
    {
        lock (_gate)
        {
            if (_missions.Get(missionId) is not { } mission || mission.Status is not (MissionStatus.Active or MissionStatus.Completed)) return false;
            var last = Steps(missionId).LastOrDefault(s => s.Value.IsDone);
            if (last is null) return false;
            var step = last.Value with
            {
                CompletedAt = null,
                Skipped = false,
                StartedAt = last.Value.StartedExplicitly ? last.Value.StartedAt : null,
            };
            _steps.Upsert(last.Id, step);
            if (mission.Status == MissionStatus.Completed)
            {
                mission = mission with { Status = MissionStatus.Active, CompletedAt = null };
                _missions.Upsert(missionId, mission);
            }
            Log(MissionEventKind.StepReopened, missionId, mission, last.Id, step);
            return true;
        }
    }

    public bool Pause(string missionId) => Hold(missionId, MissionStatus.Paused, MissionEventKind.Paused, from: [MissionStatus.Active]);

    public bool Abandon(string missionId) =>
        Hold(missionId, MissionStatus.Abandoned, MissionEventKind.Abandoned, from: [MissionStatus.Planned, MissionStatus.Active, MissionStatus.Paused]);

    private bool Hold(string missionId, MissionStatus status, MissionEventKind kind, MissionStatus[] from)
    {
        lock (_gate)
        {
            if (_missions.Get(missionId) is not { } m || !from.Contains(m.Status)) return false;
            // A planned mission has no pace to hold; a paused one keeps the time it was paused from.
            var held = m with { Status = status, PausedAt = m.StartedAt is null ? null : m.PausedAt ?? Now };
            _missions.Upsert(missionId, held);
            Log(kind, missionId, held, null, null);
            return true;
        }
    }

    /// <summary>Paused or abandoned → running again (an abandoned mission that never started goes back to planned).</summary>
    public bool Resume(string missionId)
    {
        lock (_gate)
        {
            if (_missions.Get(missionId) is not { Status: MissionStatus.Paused or MissionStatus.Abandoned } m) return false;
            var now = Now;
            var paused = m.PausedAt is { } since && now > since ? now - since : TimeSpan.Zero;
            var status = m.StartedAt is null ? MissionStatus.Planned : Current(Steps(missionId)) is null ? MissionStatus.Completed : MissionStatus.Active;
            var resumed = m with { Status = status, PausedAt = null, PausedTotal = m.PausedTotal + paused };
            _missions.Upsert(missionId, resumed);
            Log(MissionEventKind.Resumed, missionId, resumed, null, null);
            return true;
        }
    }

    /// <summary>Marks the reward of a done phase (or, with no phase, of the finished mission) as taken.</summary>
    public bool ClaimReward(string missionId, string? phaseKey = null)
    {
        lock (_gate)
        {
            if (_missions.Get(missionId) is not { } m) return false;
            var now = Now;
            if (phaseKey is null)
            {
                if (m.Status != MissionStatus.Completed || m.RewardClaimedAt is not null) return false;
                _missions.Upsert(missionId, m with { RewardClaimedAt = now });
                Log(MissionEventKind.RewardClaimed, missionId, m, null, null, m.Reward);
                return true;
            }
            var phase = m.Phase(phaseKey);
            if (phase is null || phase.RewardClaimedAt is not null) return false;
            if (!Steps(missionId).Where(s => s.Value.PhaseKey == phaseKey).All(s => s.Value.IsDone)) return false;
            _missions.Upsert(missionId, m with { Phases = m.Phases.Select(p => p.Key == phaseKey ? p with { RewardClaimedAt = now } : p).ToList() });
            Log(MissionEventKind.RewardClaimed, missionId, m, null, null, phase.Reward);
            return true;
        }
    }

    // ---- Editing -------------------------------------------------------------------------------------------------

    /// <summary>Changes the mission's own texts and deadline (status and dates are kept).</summary>
    public bool UpdateMission(string missionId, string? title = null, string? goal = null, string? reward = null, DateOnly? deadline = null, bool clearDeadline = false)
    {
        lock (_gate)
        {
            if (_missions.Get(missionId) is not { } m) return false;
            if (title is not null && MissionLimits.Clip(title, MissionLimits.Title).Length == 0) return false;
            var updated = m with
            {
                Title = title is null ? m.Title : MissionLimits.Clip(title, MissionLimits.Title),
                Goal = goal is null ? m.Goal : MissionLimits.Clip(goal, MissionLimits.Goal),
                Reward = reward is null ? m.Reward : MissionLimits.Clip(reward, MissionLimits.Reward),
                Deadline = clearDeadline ? null : deadline ?? m.Deadline,
            };
            if (updated == m) return false;
            _missions.Upsert(missionId, updated);
            return true;
        }
    }

    /// <summary>
    /// Edits a step that is not done yet (done steps keep their text: it is the record of what was done; only their
    /// note can change, through <see cref="SetNote"/>).
    /// </summary>
    public bool UpdateStep(string stepId, string title, string description, string doneWhen, double estimateDays)
    {
        lock (_gate)
        {
            if (_steps.Get(stepId) is not { IsDone: false } s || !IsOpenForChanges(s.MissionId)) return false;
            if (MissionLimits.Clip(title, MissionLimits.Title).Length == 0) return false;
            var updated = s with
            {
                Title = MissionLimits.Clip(title, MissionLimits.Title),
                Description = MissionLimits.Clip(description, MissionLimits.Description),
                DoneWhen = MissionLimits.Clip(doneWhen, MissionLimits.DoneWhen),
                EstimateDays = ClampEstimate(estimateDays),
            };
            if (updated == s) return false;
            _steps.Upsert(stepId, updated);
            return true;
        }
    }

    /// <summary>Records (or clears, with null) the task a step was sent to in another tool.</summary>
    public bool SetTask(string stepId, string? taskId)
    {
        lock (_gate)
        {
            if (_steps.Get(stepId) is not { } s || s.TaskId == taskId) return false;
            _steps.Upsert(stepId, s with { TaskId = taskId });
            return true;
        }
    }

    /// <summary>The note of any step (also a done one).</summary>
    public bool SetNote(string stepId, string note)
    {
        lock (_gate)
        {
            if (_steps.Get(stepId) is not { } s) return false;
            var clipped = MissionLimits.Clip(note, MissionLimits.Note);
            if (clipped == s.Note) return false;
            _steps.Upsert(stepId, s with { Note = clipped });
            return true;
        }
    }

    /// <summary>Adds a step at the end of a phase (default: the last phase). Not on a finished or abandoned mission.</summary>
    public string? AddStep(string missionId, StepDraft draft, string? phaseKey = null)
    {
        lock (_gate)
        {
            if (_missions.Get(missionId) is not { } m || !IsOpenForChanges(missionId) || m.Phases.Count == 0) return null;
            if (MissionLimits.Clip(draft.Title, MissionLimits.Title).Length == 0) return null;
            var steps = Steps(missionId);
            if (steps.Count >= MissionLimits.Steps) return null;
            var key = phaseKey is not null && m.Phase(phaseKey) is not null ? phaseKey : m.Phases[^1].Key;
            // After the phase's last step, before the next phase's first one.
            var phaseIndex = m.PhaseIndex(key);
            var inPhase = steps.Where(s => s.Value.PhaseKey == key).ToList();
            var after = inPhase.Count > 0 ? inPhase[^1].Value.Order : steps.LastOrDefault(s => m.PhaseIndex(s.Value.PhaseKey) < phaseIndex)?.Value.Order;
            var next = steps.FirstOrDefault(s => after is null || s.Value.Order > after)?.Value.Order;
            double order = (after, next) switch
            {
                ({ } a, { } n) => (a + n) / 2,
                ({ } a, null) => a + 1,
                (null, { } n) => n - 1,
                _ => 0,
            };
            // A step can only go after the current one: the steps before it are history.
            if (Current(steps) is { } current && order < current.Value.Order) return null;
            return _steps.Add(NewStep(missionId, key, order, draft));
        }
    }

    /// <summary>Deletes a step that is not done yet. A mission keeps at least one step.</summary>
    public bool DeleteStep(string stepId)
    {
        lock (_gate)
        {
            if (_steps.Get(stepId) is not { IsDone: false } s || !IsOpenForChanges(s.MissionId)) return false;
            if (Steps(s.MissionId).Count <= 1) return false;
            if (!_steps.Delete(stepId)) return false;
            // The last open step gone: everything left is done.
            if (_missions.Get(s.MissionId) is { Status: MissionStatus.Active } m && CurrentStep(s.MissionId) is null)
            {
                var finished = m with { Status = MissionStatus.Completed, CompletedAt = Now };
                _missions.Upsert(s.MissionId, finished);
                Log(MissionEventKind.MissionCompleted, s.MissionId, finished, null, null);
            }
            return true;
        }
    }

    private bool IsOpenForChanges(string missionId) =>
        _missions.Get(missionId) is { Status: MissionStatus.Planned or MissionStatus.Active or MissionStatus.Paused };

    /// <summary>
    /// Replaces every step not done yet (the current one too) with the steps of <paramref name="plan"/>, usually an AI's
    /// new plan for the rest of the way. Done and skipped steps stay as they are, with their phases. A phase of the
    /// plan whose title matches a phase that is not finished keeps that phase (its done steps stay in it); the others
    /// are added after the phases that have done steps. Phases left with no step are removed. The plan's deadline and
    /// note replace the mission's when it has them; its title, goal and reward do not.
    /// </summary>
    /// <returns>False when the mission is finished or abandoned, or the plan has no step.</returns>
    public bool Replan(string missionId, MissionDraft plan)
    {
        lock (_gate)
        {
            if (_missions.Get(missionId) is not { } mission || !IsOpenForChanges(missionId)) return false;
            var planPhases = plan.Phases.Where(p => p.Steps.Count > 0).ToList();
            if (planPhases.Count == 0) return false;
            var steps = Steps(missionId);
            var done = steps.Where(s => s.Value.IsDone).ToList();
            if (done.Count + planPhases.Sum(p => p.Steps.Count) > MissionLimits.Steps) return false;

            // Phases that keep done steps stay first, in their order.
            var donePhaseKeys = done.Select(s => s.Value.PhaseKey).ToHashSet(StringComparer.Ordinal);
            var finishedKeys = mission.Phases.Where(p => steps.Where(s => s.Value.PhaseKey == p.Key).All(s => s.Value.IsDone)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var order = mission.Phases.Where(p => donePhaseKeys.Contains(p.Key)).ToList();
            var newSteps = new List<(string Key, StepDraft Step)>();
            foreach (var p in planPhases)
            {
                var title = MissionLimits.Clip(p.Title, MissionLimits.Title);
                // Same title as a phase still under way (or to come): the plan continues it.
                var existing = mission.Phases.FirstOrDefault(x => !finishedKeys.Contains(x.Key)
                    && string.Equals(x.Title.Trim(), title, StringComparison.CurrentCultureIgnoreCase));
                MissionPhase phase;
                if (existing is not null && order.FirstOrDefault(x => x.Key == existing.Key) is { } kept)
                    phase = kept;
                else if (existing is not null && order.All(x => x.Key != existing.Key))
                {
                    phase = existing with { Reward = p.Reward.Length > 0 ? MissionLimits.Clip(p.Reward, MissionLimits.Reward) : existing.Reward };
                    order.Add(phase);
                }
                else
                {
                    phase = new MissionPhase
                    {
                        Key = NewKey(),
                        Title = title.Length > 0 ? title : $"Phase {order.Count + 1}",
                        Reward = MissionLimits.Clip(p.Reward, MissionLimits.Reward),
                    };
                    order.Add(phase);
                }
                newSteps.AddRange(p.Steps.Select(s => (phase.Key, s)));
            }
            if (order.Count > MissionLimits.Phases) return false;

            foreach (var s in steps.Where(s => !s.Value.IsDone)) _steps.Delete(s.Id);
            // New steps come after the done ones, phase by phase in the new order.
            var next = done.Count == 0 ? 0 : done.Max(s => s.Value.Order) + 1;
            foreach (var phase in order)
                foreach (var (key, step) in newSteps.Where(s => s.Key == phase.Key))
                    _steps.Add(NewStep(missionId, key, next++, step));

            var updated = mission with
            {
                Phases = order,
                Deadline = plan.Deadline ?? mission.Deadline,
                Note = plan.Note.Length > 0 ? MissionLimits.Clip(plan.Note, MissionLimits.Note) : mission.Note,
            };
            _missions.Upsert(missionId, updated);
            return true;
        }
    }

    /// <summary>Moves a mission up (-1) or down (+1) in the picker.</summary>
    public bool Move(string missionId, int direction)
    {
        lock (_gate)
        {
            var list = Missions().ToList();
            var index = list.FindIndex(m => m.Id == missionId);
            var target = index + Math.Sign(direction);
            if (index < 0 || direction == 0 || target < 0 || target >= list.Count) return false;
            (list[index], list[target]) = (list[target], list[index]);
            for (var i = 0; i < list.Count; i++)
                if (list[i].Value.Order != i) _missions.Upsert(list[i].Id, list[i].Value with { Order = i });
            return true;
        }
    }

    /// <summary>Deletes a mission and its steps from every device. The history stays.</summary>
    public bool Delete(string missionId)
    {
        lock (_gate)
        {
            foreach (var step in Steps(missionId)) _steps.Delete(step.Id);
            return _missions.Delete(missionId);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private bool TryCurrent(string missionId, out Mission mission, out SyncedItem<MissionStep> current)
    {
        mission = _missions.Get(missionId)!;
        current = null!;
        if (mission is not { Status: MissionStatus.Active }) return false;
        if (CurrentStep(missionId) is not { } step) return false;
        current = step;
        return true;
    }

    private static int IndexOf(IReadOnlyList<SyncedItem<MissionStep>> steps, string id)
    {
        for (var i = 0; i < steps.Count; i++)
            if (steps[i].Id == id) return i;
        return -1;
    }

    private void Log(MissionEventKind kind, string missionId, Mission mission, string? stepId, MissionStep? step, string note = "") =>
        _history.Append(new MissionEvent
        {
            Kind = kind,
            At = Now,
            MissionId = missionId,
            MissionTitle = mission.Title,
            StepId = stepId,
            StepTitle = step?.Title ?? "",
            PhaseKey = step?.PhaseKey,
            Note = note,
        });
}
