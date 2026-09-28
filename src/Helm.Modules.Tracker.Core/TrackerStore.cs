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

    /// <summary>Workspaces in display order.</summary>
    public IReadOnlyList<SyncedItem<TrackerWorkspace>> Workspaces() =>
        _workspaces.All().OrderBy(w => w.Value.Order).ThenBy(w => w.Value.CreatedAt).ThenBy(w => w.Id, StringComparer.Ordinal).ToList();

    public TrackerWorkspace? GetWorkspace(string id) => _workspaces.Get(id);

    public string AddWorkspace(string name, WorkspaceKind kind, string currency = "")
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A workspace needs a name.", nameof(name));
        lock (_gate)
        {
            var existing = _workspaces.All();
            var order = existing.Count == 0 ? 0 : existing.Max(w => w.Value.Order) + 1;
            return _workspaces.Add(new TrackerWorkspace
            {
                Name = name,
                Kind = kind,
                Currency = currency.Trim(),
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

    /// <summary>Every item of every existing workspace (items of deleted workspaces are left out).</summary>
    public IReadOnlyList<SyncedItem<TrackerItem>> AllItems()
    {
        var workspaces = _workspaces.All().Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        return _items.All().Where(i => workspaces.Contains(i.Value.WorkspaceId)).ToList();
    }

    /// <summary>Open items in list order: priority (urgent first), then manual order, then age.</summary>
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
            var title = draft.Title.Trim();
            var person = draft.Person.Trim();
            if (workspace.Kind == WorkspaceKind.Tasks && title.Length == 0) throw new ArgumentException("A task needs a title.", nameof(draft));
            if (workspace.Kind == WorkspaceKind.Debts && person.Length == 0 && title.Length == 0)
                throw new ArgumentException("A debt needs a person or a description.", nameof(draft));

            var siblings = Items(workspaceId).Where(i => !i.Value.IsCompleted && i.Value.Priority == draft.Priority).ToList();
            var item = new TrackerItem
            {
                WorkspaceId = workspaceId,
                Title = title,
                Notes = draft.Notes.Trim(),
                Priority = draft.Priority,
                Order = siblings.Count == 0 ? 0 : siblings.Max(i => i.Value.Order) + 1,
                DueDate = draft.DueDate,
                CreatedAt = Now,
                Person = person,
                Amount = Math.Abs(draft.Amount),
                Direction = draft.Direction,
            };
            var id = _items.Add(item);
            Log(TrackerEventKind.Created, id, item, workspace.Kind);
            return id;
        }
    }

    /// <summary>Edits the fields the user can change (title, notes, priority, due date, debt details).</summary>
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
            updated = updated with { Title = updated.Title.Trim(), Person = updated.Person.Trim(), Notes = updated.Notes.Trim(), Amount = Math.Abs(updated.Amount) };
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
            var done = item with { CompletedAt = now, StartedAt = item.StartedAt ?? item.CreatedAt };
            _items.Upsert(id, done);
            Log(TrackerEventKind.Completed, id, done);
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
            Log(TrackerEventKind.Deleted, id, item);
            return _items.Delete(id);
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
        items.Where(i => !i.Value.IsCompleted)
            .OrderByDescending(i => i.Value.Priority)
            .ThenBy(i => i.Value.Order)
            .ThenBy(i => i.Value.CreatedAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<SyncedItem<TrackerItem>> Completed(IEnumerable<SyncedItem<TrackerItem>> items) =>
        items.Where(i => i.Value.IsCompleted)
            .OrderByDescending(i => i.Value.CompletedAt)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();
}
