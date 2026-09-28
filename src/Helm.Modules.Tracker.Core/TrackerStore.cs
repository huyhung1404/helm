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

    /// <summary>True when the (single) debt book exists.</summary>
    public bool HasDebtBook => _workspaces.All().Any(w => w.Value.Kind == WorkspaceKind.Debts);

    /// <summary>Currency of a new debt book unless the caller gives one.</summary>
    public const string DefaultCurrency = "₫";

    /// <exception cref="InvalidOperationException">
    /// A debt book already exists: there is one debt book, with everyone in it (people are a field of each debt).
    /// </exception>
    public string AddWorkspace(string name, WorkspaceKind kind, string? currency = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A workspace needs a name.", nameof(name));
        lock (_gate)
        {
            var existing = _workspaces.All();
            if (kind == WorkspaceKind.Debts && existing.FirstOrDefault(w => w.Value.Kind == WorkspaceKind.Debts) is { } book)
                throw new InvalidOperationException($"You already have a debt book, \u201c{book.Value.Name}\u201d. Add everyone to it: each debt has its own person.");
            var order = existing.Count == 0 ? 0 : existing.Max(w => w.Value.Order) + 1;
            return _workspaces.Add(new TrackerWorkspace
            {
                Name = name,
                Kind = kind,
                Currency = (currency ?? (kind == WorkspaceKind.Debts ? DefaultCurrency : "")).Trim(),
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
            if (draft.ParentId is { } parentId)
            {
                if (_items.Get(parentId) is not { } parent || parent.WorkspaceId != workspaceId || parent.IsSubtask)
                    throw new InvalidOperationException("That task no longer exists.");
                if (workspace.Kind != WorkspaceKind.Tasks) throw new InvalidOperationException("Only tasks have subtasks.");
            }
            var title = draft.Title.Trim();
            var person = draft.Person.Trim();
            if (workspace.Kind == WorkspaceKind.Tasks && title.Length == 0) throw new ArgumentException("A task needs a title.", nameof(draft));
            if (workspace.Kind == WorkspaceKind.Debts && person.Length == 0 && title.Length == 0)
                throw new ArgumentException("A debt needs a person or a description.", nameof(draft));

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

    // ---- Debt book -----------------------------------------------------------------------------------------------

    /// <summary>What a person owes the user (+) or the user owes them (-), over their open entries.</summary>
    public decimal DebtBalance(string workspaceId, string person) =>
        OpenDebts(workspaceId, person).Sum(i => i.Value.SignedAmount);

    private List<SyncedItem<TrackerItem>> OpenDebts(string workspaceId, string person)
    {
        var key = DebtLedger.Key(person);
        return Items(workspaceId).Where(i => !i.Value.IsCompleted && DebtLedger.Key(i.Value.Person) == key).ToList();
    }

    /// <summary>
    /// Adds money to a person in the debt book: owes me (+), I owe (-) or a repayment (toward 0). The same person
    /// (whatever the case and spacing of the name) shares one balance; when it reaches 0 every open entry of that person
    /// is settled together. Each entry is its own record and history event, so every change of the amount is in the
    /// history.
    /// </summary>
    /// <returns>The new entry's id.</returns>
    public string AddDebt(string workspaceId, string person, decimal amount, DebtEntryKind kind, string note = "", DateTimeOffset? dueAt = null)
    {
        lock (_gate)
        {
            var workspace = _workspaces.Get(workspaceId) ?? throw new InvalidOperationException("That workspace no longer exists.");
            if (workspace.Kind != WorkspaceKind.Debts) throw new InvalidOperationException("Debts go in the debt book.");
            person = DebtLedger.Clean(person);
            if (person.Length == 0) throw new ArgumentException("Who is it with? A debt needs a person.", nameof(person));
            amount = Math.Abs(amount);
            if (amount == 0) throw new ArgumentException("Enter an amount.", nameof(amount));

            var open = OpenDebts(workspaceId, person);
            var balance = open.Sum(i => i.Value.SignedAmount);
            // The name as it was first written, so one person does not show up twice.
            if (open.Count > 0) person = open.OrderBy(i => i.Value.CreatedAt).First().Value.Person;
            DebtDirection direction;
            switch (kind)
            {
                case DebtEntryKind.Repayment when balance == 0:
                    throw new InvalidOperationException($"{person} has nothing to repay: the balance is 0.");
                case DebtEntryKind.Repayment:
                    direction = balance > 0 ? DebtDirection.IOwe : DebtDirection.TheyOweMe;
                    break;
                case DebtEntryKind.IOwe:
                    direction = DebtDirection.IOwe;
                    break;
                default:
                    direction = DebtDirection.TheyOweMe;
                    break;
            }
            var due = dueAt ?? open.Select(i => i.Value.DueAt).Where(d => d is not null).Max();
            var item = new TrackerItem
            {
                WorkspaceId = workspaceId,
                Title = note.Trim(),
                Person = person,
                Amount = amount,
                Direction = direction,
                IsRepayment = kind == DebtEntryKind.Repayment,
                DueAt = due,
                DueDate = due is { } at ? TrackerDue.Day(at, TimeZoneInfo.Local) : open.Select(i => i.Value.DueDate).Where(d => d is not null).Max(),
                CreatedAt = Now,
            };
            var id = _items.Add(item);
            Log(TrackerEventKind.Created, id, item, WorkspaceKind.Debts);

            if (balance + item.SignedAmount == 0)
            {
                var now = Now;
                foreach (var (entryId, entry, _) in OpenDebts(workspaceId, person)) CompleteOne(entryId, entry, now);
            }
            return id;
        }
    }

    /// <summary>Sets (or clears) when a person's open debts are due: every open entry of that person gets the new time.</summary>
    public int SetDebtDue(string workspaceId, string person, DateTimeOffset? dueAt)
    {
        lock (_gate)
        {
            var open = OpenDebts(workspaceId, person);
            var day = TrackerDue.Day(dueAt, TimeZoneInfo.Local);
            foreach (var (id, entry, _) in open)
            {
                var updated = entry with { DueAt = dueAt, DueDate = day };
                if (updated != entry) _items.Upsert(id, updated);
            }
            return open.Count;
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
