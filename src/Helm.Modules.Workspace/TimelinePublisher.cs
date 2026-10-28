using Helm.Core.Sync;

namespace Helm.Modules.Workspace;

/// <summary>
/// Writes the timelines of this PC's sessions as pages (<see cref="WorkspaceSync.Timeline"/>): one
/// <see cref="TimelineLedger"/> per session, and only the pages whose steps changed are written again (in a live
/// session, the last one). Pages of sessions no longer shown are deleted. Called under the relay's lock.
/// </summary>
internal sealed class TimelinePublisher(ISyncedCollection<WorkspaceTimelinePage> pages, string machineId, TimeProvider time)
{
    private readonly Dictionary<string, TimelineLedger> _ledgers = new(StringComparer.Ordinal);

    /// <summary>The pages each session's record last pointed at: when they move, the pages outside are deleted.</summary>
    private readonly Dictionary<string, (string Epoch, int First, int Last)> _shapes = new(StringComparer.Ordinal);

    /// <summary>Sessions seen without pages whose leftover pages were cleared.</summary>
    private readonly HashSet<string> _unpaged = new(StringComparer.Ordinal);

    /// <summary>
    /// Pages <paramref name="steps"/> (the latest read of the transcript) and returns <paramref name="record"/> pointing
    /// at the pages, its own steps cut to the newest few. A timeline that cannot be paged (steps without ids) goes in the
    /// record whole, as before.
    /// </summary>
    public WorkspaceSessionRecord Publish(WorkspaceSessionRecord record, IReadOnlyList<TranscriptItem> steps)
    {
        if (!_ledgers.TryGetValue(record.SessionId, out var ledger)) _ledgers[record.SessionId] = ledger = new TimelineLedger();
        var read = steps.Select(i => i with { Text = WorkspaceSync.Cut(i.Text) ?? "", CallId = null }).ToList();
        if (!ledger.Merge(read) || ledger.Count == 0)
        {
            // Once for a session without pages (and when one stops paging): none of its pages is left behind.
            if (_shapes.Remove(record.SessionId) | _unpaged.Add(record.SessionId)) DeletePages(record.SessionId, keepFrom: 1, keepTo: 0);
            return record;
        }
        var now = time.GetLocalNow();
        // Only the pages whose steps changed are measured and written again (in a live session, the last one).
        foreach (var p in ledger.TakeChangedPages())
        {
            var items = TimelineLedger.Fit(ledger.Page(p));
            var id = WorkspaceSync.PageId(record.SessionId, p);
            if (pages.Get(id) is { } old && old.Epoch == ledger.Epoch && old.MachineId == machineId && old.Items.SequenceEqual(items)) continue;
            pages.Upsert(id, new WorkspaceTimelinePage { MachineId = machineId, SessionId = record.SessionId, Epoch = ledger.Epoch, Page = p, Items = items, UpdatedAt = now });
        }
        var shape = (ledger.Epoch, ledger.FirstPage, ledger.LastPage);
        if (!_shapes.TryGetValue(record.SessionId, out var was) || was != shape)
        {
            DeletePages(record.SessionId, ledger.FirstPage, ledger.LastPage);
            _shapes[record.SessionId] = shape;
        }
        return record with
        {
            Items = record.Items.TakeLast(WorkspaceSync.LegacyItems).ToList(),
            TimelineEpoch = ledger.Epoch,
            TimelineFirstPage = ledger.FirstPage,
            TimelineLastPage = ledger.LastPage,
        };
    }

    /// <summary>Deletes the pages and ledgers of this PC's sessions that are not in <paramref name="shown"/>.</summary>
    public void KeepOnly(IReadOnlySet<string> shown)
    {
        foreach (var gone in _ledgers.Keys.Where(id => !shown.Contains(id)).ToList())
        {
            _ledgers.Remove(gone);
            _shapes.Remove(gone);
            _unpaged.Remove(gone);
        }
        foreach (var item in pages.All().Where(i => i.Value.MachineId == machineId && !shown.Contains(i.Value.SessionId)).ToList())
            pages.Delete(item.Id);
    }

    /// <summary>The pages of this PC's session outside <paramref name="keepFrom"/>..<paramref name="keepTo"/>.</summary>
    private void DeletePages(string sessionId, int keepFrom, int keepTo)
    {
        foreach (var item in pages.All().Where(i => i.Value.MachineId == machineId && i.Value.SessionId == sessionId && (i.Value.Page < keepFrom || i.Value.Page > keepTo)).ToList())
            pages.Delete(item.Id);
    }
}
