using Helm.Core.Sync;

namespace Helm.Modules.Tracker;

/// <summary>
/// Workspaces, items and history on top of Helm Sync. Every write is local and immediate (the sync engine uploads it
/// in the background), and every state change of an item is also appended to the history log for reports.
/// Callable from any thread; <see cref="Changed"/> may be raised on a background thread after a sync.
/// </summary>
public sealed class TrackerStore
{
    public const string WorkspacesCollection = "tracker.workspaces";
    public const string ItemsCollection = "tracker.items";
    public const string HistoryCollection = "tracker.history";

    private readonly ISyncedCollection<TrackerWorkspace> _workspaces;
    private readonly ISyncedCollection<TrackerItem> _items;
    private readonly ISyncedLog<TrackerEvent> _history;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public TrackerStore(
        ISyncedCollection<TrackerWorkspace> workspaces,
        ISyncedCollection<TrackerItem> items,
        ISyncedLog<TrackerEvent> history,
        TimeProvider? time = null)
    {
        _workspaces = workspaces;
        _items = items;
        _history = history;
        _time = time ?? TimeProvider.System;
        _workspaces.Changed += OnChanged;
        _items.Changed += OnChanged;
        _history.Changed += OnChanged;
    }

    /// <summary>Raised after any local write or synced change (on the writing thread or a thread-pool thread).</summary>
    public event EventHandler? Changed;

    public DateTimeOffset Now => _time.GetUtcNow();

    // ---- Workspaces ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The to-do lists in display order. The old debt book (<see cref="WorkspaceKind.Debts"/>) is left out: its entries
    /// are in Wallet's debt book now.
    /// </summary>
    public IReadOnlyList<SyncedItem<TrackerWorkspace>> Workspaces() =>
        _workspaces.All().Where(w => w.Value.Kind == WorkspaceKind.Tasks)
            .OrderBy(w => w.Value.Order).ThenBy(w => w.Value.CreatedAt).ThenBy(w => w.Id, StringComparer.Ordinal).ToList();

    public TrackerWorkspace? GetWorkspace(string id) => _workspaces.Get(id);

    /// <summary>What adding to the old debt book says: debts moved to Wallet.</summary>
    public const string DebtsMoved = "Debts are kept in Wallet now: open Wallet, then Debts.";

    /// <summary>Adds a to-do list.</summary>
    /// <exception cref="InvalidOperationException">A debt book was asked for: debts are kept in Wallet now.</exception>
    public string AddWorkspace(string name, WorkspaceKind kind = WorkspaceKind.Tasks)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A workspace needs a name.", nameof(name));
        if (kind != WorkspaceKind.Tasks) throw new InvalidOperationException(DebtsMoved);
        lock (_gate)
        {
            var existing = _workspaces.All();
            var order = existing.Count == 0 ? 0 : existing.Max(w => w.Value.Order) + 1;
            return _workspaces.Add(new TrackerWorkspace
            {
                Name = name,
                Kind = kind,
                Order = order,
                CreatedAt = Now,
            });
        }
    }

    /// <returns>False when the workspace no longer exists.</returns>
    public bool UpdateWorkspace(string id, Func<TrackerWorkspace, TrackerWorkspace> change)
    {
        lock (_gate)
        {
            if (_workspaces.Get(id) is not { } current) return false;
            var updated = change(current);
            if (updated == current) return true;
            _workspaces.Upsert(id, updated);
            return true;
        }
    }

    /// <summary>Deletes the workspace and its items. The history stays, so past reports are unchanged.</summary>
    public bool DeleteWorkspace(string id)
    {
        lock (_gate)
        {
            foreach (var item in _items.All().Where(i => i.Value.WorkspaceId == id)) _items.Delete(item.Id);
            return _workspaces.Delete(id);
        }
    }

    // ---- Items ---------------------------------------------------------------------------------------------------

    public TrackerItem? GetItem(string id) => _items.Get(id);

    /// <summary>All items of a workspace (open and completed), unordered.</summary>
    public IReadOnlyList<SyncedItem<TrackerItem>> Items(string workspaceId) =>
        _items.All().Where(i => i.Value.WorkspaceId == workspaceId).ToList();

    /// <summary>Every item of every to-do list (items of deleted workspaces and of the old debt book are left out).</summary>
    public IReadOnlyList<SyncedItem<TrackerItem>> AllItems()
    {
        var workspaces = Workspaces().Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        return _items.All().Where(i => workspaces.Contains(i.Value.WorkspaceId)).ToList();
    }

    /// <summary>A task's subtasks (open first, then done), in the order they were added.</summary>
    public IReadOnlyList<SyncedItem<TrackerItem>> Subtasks(string parentId) =>
        _items.All().Where(i => i.Value.ParentId == parentId)
            .OrderBy(i => i.Value.IsCompleted).ThenBy(i => i.Value.Order).ThenBy(i => i.Value.CreatedAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();

    /// <summary>Open items in list order: priority (urgent first), then manual order, then age. Subtasks are listed under their task.</summary>
    public IReadOnlyList<SyncedItem<TrackerItem>> OpenItems(string workspaceId) =>
        TrackerOrdering.Open(Items(workspaceId));

    /// <summary>Completed items, most recently completed first.</summary>
    public IReadOnlyList<SyncedItem<TrackerItem>> CompletedItems(string workspaceId) =>
        TrackerOrdering.Completed(Items(workspaceId));

    public string AddItem(string workspaceId, TrackerItemDraft draft)
    {
        lock (_gate)
        {
            var workspace = _workspaces.Get(workspaceId) ?? throw new InvalidOperationException("That workspace no longer exists.");
            if (workspace.Kind != WorkspaceKind.Tasks) throw new InvalidOperationException(DebtsMoved);
            if (draft.ParentId is { } parentId)
            {
                if (_items.Get(parentId) is not { } parent || parent.WorkspaceId != workspaceId || parent.IsSubtask)
                    throw new InvalidOperationException("That task no longer exists.");
                if (workspace.Kind != WorkspaceKind.Tasks) throw new InvalidOperationException("Only tasks have subtasks.");
            }
            var title = draft.Title.Trim();
            if (title.Length == 0) throw new ArgumentException("A task needs a title.", nameof(draft));

            var siblings = draft.ParentId is { } p
                ? Subtasks(p)
                : Items(workspaceId).Where(i => !i.Value.IsCompleted && !i.Value.IsSubtask && i.Value.Priority == draft.Priority).ToList();
            var item = new TrackerItem
            {
                WorkspaceId = workspaceId,
                ParentId = draft.ParentId,
                Title = title,
                Notes = draft.Notes.Trim(),
                Priority = draft.Priority,
                Order = siblings.Count == 0 ? 0 : siblings.Max(i => i.Value.Order) + 1,
                DueAt = draft.DueAt,
                DueDate = draft.DueAt is { } at ? TrackerDue.Day(at, TimeZoneInfo.Local) : draft.DueDate,
                CreatedAt = Now,
            };
            string id;
            if (draft.RepeatDaily && draft.ParentId is null)
            {
                // A repeating task: today's occurrence of a new series.
                var today = Today();
                var series = Guid.NewGuid().ToString("N");
                item = item with { SeriesId = series, OccurrenceDate = today, RepeatDaily = true, RepeatUntil = draft.RepeatUntil };
                id = OccurrenceId(series, today);
                _items.Upsert(id, item);
            }
            else
            {
                id = _items.Add(item);
            }
            Log(TrackerEventKind.Created, id, item, workspace.Kind);
            return id;
        }
    }

    // ---- Repeating tasks -----------------------------------------------------------------------------------------

    public static string OccurrenceId(string seriesId, DateOnly day) => $"{seriesId}@{day:yyyy-MM-dd}";

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, TimeZoneInfo.Local).DateTime);

    /// <summary>
    /// Makes today's task of every repeating series that goes on (its latest day still repeats and today is not past
    /// its last day). Days the app was not used are skipped: only today's task appears. The id is the same on every
    /// device, so two devices doing this at once write one record.
    /// </summary>
    /// <returns>How many tasks were made.</returns>
    public int EnsureRepeats(DateOnly? day = null)
    {
        var today = day ?? Today();
        var made = 0;
        lock (_gate)
        {
            var workspaces = _workspaces.All().Where(w => w.Value.Kind == WorkspaceKind.Tasks).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            var all = _items.All();
            foreach (var series in all.Where(i => i.Value.SeriesId is not null && i.Value.OccurrenceDate is not null && !i.Value.IsSubtask)
                         .GroupBy(i => i.Value.SeriesId!))
            {
                var latest = series.OrderByDescending(i => i.Value.OccurrenceDate).First();
                var last = latest.Value;
                if (!last.RepeatDaily || last.OccurrenceDate >= today || !workspaces.Contains(last.WorkspaceId)) continue;
                if (last.RepeatUntil is { } until && today > until) continue;
                var id = OccurrenceId(series.Key, today);
                if (_items.Get(id) is not null) continue;

                DateTimeOffset? dueAt = null;
                if (last.DueAt is { } at)
                {
                    var local = today.ToDateTime(TimeOnly.FromTimeSpan(TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).TimeOfDay));
                    dueAt = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
                }
                var next = new TrackerItem
                {
                    WorkspaceId = last.WorkspaceId,
                    Title = last.Title,
                    Notes = last.Notes,
                    Priority = last.Priority,
                    Order = last.Order,
                    SeriesId = last.SeriesId,
                    OccurrenceDate = today,
                    RepeatDaily = true,
                    RepeatUntil = last.RepeatUntil,
                    DueAt = dueAt,
                    DueDate = last.DueAt is not null || last.DueDate is not null ? today : null,
                    CreatedAt = Now,
                };
                _items.Upsert(id, next);
                Log(TrackerEventKind.Created, id, next, WorkspaceKind.Tasks);
                // The same subtasks, not done yet.
                var index = 0;
                foreach (var (_, sub, _) in Subtasks(latest.Id))
                {
                    var subId = $"{id}/{index++}";
                    if (_items.Get(subId) is not null) continue;
                    _items.Upsert(subId, new TrackerItem
                    {
                        WorkspaceId = sub.WorkspaceId,
                        ParentId = id,
                        Title = sub.Title,
                        Priority = sub.Priority,
                        Order = sub.Order,
                        CreatedAt = Now,
                    });
                }
                made++;
            }
        }
        return made;
    }

    /// <summary>How many days of a repeating task were done (over all its occurrences that still exist).</summary>
    public int SeriesCompletions(string seriesId) =>
        _items.All().Count(i => i.Value.SeriesId == seriesId && i.Value.IsCompleted && !i.Value.IsSubtask);

    /// <summary>Stops a series: no new day appears (the tasks already there stay).</summary>
    public void StopRepeating(string seriesId)
    {
        lock (_gate)
        {
            foreach (var (id, item, _) in _items.All().Where(i => i.Value.SeriesId == seriesId && i.Value.RepeatDaily).ToList())
                _items.Upsert(id, item with { RepeatDaily = false });
        }
    }

    /// <summary>Edits the fields the user can change (title, notes, priority, due date).</summary>
    public bool UpdateItem(string id, Func<TrackerItem, TrackerItem> change)
    {
        lock (_gate)
        {
            if (_items.Get(id) is not { } current) return false;
            var updated = change(current) with
            {
                // Identity and timestamps are owned by the store.
                WorkspaceId = current.WorkspaceId,
                CreatedAt = current.CreatedAt,
                StartedAt = current.StartedAt,
                CompletedAt = current.CompletedAt,
                StartedExplicitly = current.StartedExplicitly,
            };
            updated = updated with { Title = updated.Title.Trim(), Notes = updated.Notes.Trim() };
            // The day older versions read follows the due time.
            if (updated.DueAt is { } at) updated = updated with { DueDate = TrackerDue.Day(at, TimeZoneInfo.Local) };
            if (updated.Priority != current.Priority && !current.IsCompleted)
            {
                // Moving to another priority puts the item at the end of that group.
                var siblings = Items(current.WorkspaceId).Where(i => i.Id != id && !i.Value.IsCompleted && i.Value.Priority == updated.Priority).ToList();
                updated = updated with { Order = siblings.Count == 0 ? 0 : siblings.Max(i => i.Value.Order) + 1 };
            }
            if (updated == current) return true;
            _items.Upsert(id, updated);
            return true;
        }
    }

    /// <summary>Marks the start of work now. No-op for completed or already started items.</summary>
    public bool Start(string id)
    {
        lock (_gate)
        {
            if (_items.Get(id) is not { } item || item.IsCompleted || item.StartedAt is not null) return false;
            var started = item with { StartedAt = Now, StartedExplicitly = true };
            _items.Upsert(id, started);
            Log(TrackerEventKind.Started, id, started);
            return true;
        }
    }

    /// <summary>Marks the item done now and records it in the history (with its created/started/completed times).</summary>
    public bool Complete(string id)
    {
        lock (_gate)
        {
            if (_items.Get(id) is not { } item || item.IsCompleted) return false;
            var now = Now;
            CompleteOne(id, item, now);
            // Finishing a task finishes what is left of it.
            foreach (var (subId, sub, _) in Subtasks(id).Where(x => !x.Value.IsCompleted)) CompleteOne(subId, sub, now);
            return true;
        }
    }

    /// <summary>Moves a completed item back to the open list (its original start time is kept).</summary>
    public bool Reopen(string id)
    {
        lock (_gate)
        {
            if (_items.Get(id) is not { } item || !item.IsCompleted) return false;
            var siblings = Items(item.WorkspaceId).Where(i => !i.Value.IsCompleted && i.Value.Priority == item.Priority).ToList();
            var open = item with
            {
                CompletedAt = null,
                // A start time that was only filled in by completion is dropped again.
                StartedAt = item.StartedExplicitly ? item.StartedAt : null,
                Order = siblings.Count == 0 ? 0 : siblings.Max(i => i.Value.Order) + 1,
            };
            _items.Upsert(id, open);
            Log(TrackerEventKind.Reopened, id, open);
            return true;
        }
    }

    public bool DeleteItem(string id)
    {
        lock (_gate)
        {
            if (_items.Get(id) is not { } item) return false;
            // Deleting a day of a repeating task ends the series (otherwise it would come back tomorrow).
            if (item.SeriesId is { } series) StopRepeating(series);
            foreach (var (subId, sub, _) in Subtasks(id))
            {
                Log(TrackerEventKind.Deleted, subId, sub);
                _items.Delete(subId);
            }
            Log(TrackerEventKind.Deleted, id, item);
            return _items.Delete(id);
        }
    }

    private void CompleteOne(string id, TrackerItem item, DateTimeOffset now)
    {
        var done = item with { CompletedAt = now, StartedAt = item.StartedAt ?? item.CreatedAt };
        _items.Upsert(id, done);
        Log(TrackerEventKind.Completed, id, done);
    }

    // ---- The old debt book ---------------------------------------------------------------------------------------

    /// <summary>
    /// Entries of the old debt book (Helm 0.26 and older) not yet copied into Wallet's debt book. Older versions may
    /// still add some on a device that was not updated.
    /// </summary>
    public IReadOnlyList<(string Id, TrackerItem Item)> DebtsToMove()
    {
        var books = _workspaces.All().Where(w => w.Value.Kind == WorkspaceKind.Debts).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        if (books.Count == 0) return [];
        return _items.All().Where(i => books.Contains(i.Value.WorkspaceId) && !i.Value.MovedToWallet).Select(i => (i.Id, i.Value)).ToList();
    }

    /// <summary>Marks old debt entries as copied into Wallet, so they are not copied again (a deletion there stays).</summary>
    public void MarkMoved(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            foreach (var id in ids)
                if (_items.Get(id) is { MovedToWallet: false } item) _items.Upsert(id, item with { MovedToWallet = true });
        }
    }

    /// <summary>
    /// Moves an open item up (<paramref name="delta"/> &lt; 0) or down within its priority group.
    /// </summary>
    /// <returns>False at the edge of the group, or when the item is not open.</returns>
    public bool Move(string id, int delta)
    {
        if (delta == 0) return false;
        lock (_gate)
        {
            if (_items.Get(id) is not { } item || item.IsCompleted) return false;
            var group = OpenItems(item.WorkspaceId).Where(i => i.Value.Priority == item.Priority).ToList();
            var index = group.FindIndex(i => i.Id == id);
            var target = index + Math.Sign(delta);
            if (index < 0 || target < 0 || target >= group.Count) return false;

            // Renumber the group so equal orders (e.g. from two devices) cannot make a swap a no-op.
            var ids = group.Select(i => i.Id).ToList();
            (ids[index], ids[target]) = (ids[target], ids[index]);
            for (var i = 0; i < ids.Count; i++)
            {
                var current = group.First(g => g.Id == ids[i]).Value;
                if (current.Order != i) _items.Upsert(ids[i], current with { Order = i });
            }
            return true;
        }
    }

    // ---- History -------------------------------------------------------------------------------------------------

    /// <summary>All history events in the order they happened.</summary>
    public IReadOnlyList<TrackerEvent> History() =>
        _history.All().Select(e => e.Value).OrderBy(e => e.At).ToList();

    private void Log(TrackerEventKind kind, string id, TrackerItem item, WorkspaceKind? workspaceKind = null)
    {
        var wk = workspaceKind ?? _workspaces.Get(item.WorkspaceId)?.Kind ?? WorkspaceKind.Tasks;
        _history.Append(TrackerEvent.For(kind, Now, id, item, wk));
    }

    private void OnChanged(object? sender, SyncedChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>List order rules, shared by the pages and the Android widget.</summary>
public static class TrackerOrdering
{
    public static IReadOnlyList<SyncedItem<TrackerItem>> Open(IEnumerable<SyncedItem<TrackerItem>> items) =>
        items.Where(i => !i.Value.IsCompleted && !i.Value.IsSubtask)
            .OrderByDescending(i => i.Value.Priority)
            .ThenBy(i => i.Value.Order)
            .ThenBy(i => i.Value.CreatedAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<SyncedItem<TrackerItem>> Completed(IEnumerable<SyncedItem<TrackerItem>> items) =>
        items.Where(i => i.Value.IsCompleted && !i.Value.IsSubtask)
            .OrderByDescending(i => i.Value.CompletedAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();
}
