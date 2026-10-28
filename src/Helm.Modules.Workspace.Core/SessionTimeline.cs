using Helm.Core.Sync;

namespace Helm.Modules.Workspace;

/// <summary>
/// The phone's view of a session's steps: its timeline pages put back together, oldest first, or the record's own
/// steps when its PC does not page (older PCs, transcripts without step ids). A page that has not arrived yet ends the
/// run at its place; the record's newest steps, written with the last page, fill in what that page would add.
/// </summary>
public sealed class SessionTimeline(ISyncedCollection<WorkspaceTimelinePage>? pages)
{
    public IReadOnlyList<TranscriptItem> ItemsOf(WorkspaceSessionRecord s)
    {
        if (pages is null || s.TimelineEpoch is not { } epoch) return s.Items;
        var runs = new List<IReadOnlyList<TranscriptItem>>();
        var p = s.TimelineLastPage;
        // The newest page may be a moment behind the record: start below it then.
        while (p >= s.TimelineFirstPage && Page(s, p, epoch) is null) p--;
        for (; p >= s.TimelineFirstPage && Page(s, p, epoch) is { } page; p--) runs.Add(page.Items);
        if (runs.Count == 0) return s.Items;
        runs.Reverse();
        var items = runs.SelectMany(r => r).ToList();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
            if (items[i].Id is { } id) at[id] = i;
        // The record's steps are as new as the last page, or newer.
        foreach (var item in s.Items)
        {
            if (item.Id is { } id && at.TryGetValue(id, out var i)) items[i] = item;
            else items.Add(item);
        }
        return items;
    }

    /// <summary>The pages of <paramref name="sessionId"/> are among <paramref name="ids"/> (a change worth redrawing).</summary>
    public static bool Touches(IEnumerable<string> ids, string sessionId) => ids.Any(id => id.StartsWith(sessionId + ".", StringComparison.Ordinal));

    private WorkspaceTimelinePage? Page(WorkspaceSessionRecord s, int page, string epoch) =>
        pages!.Get(WorkspaceSync.PageId(s.SessionId, page)) is { } found && found.Epoch == epoch && found.MachineId == s.MachineId ? found : null;
}
