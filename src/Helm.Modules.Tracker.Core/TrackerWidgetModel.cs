namespace Helm.Modules.Tracker;

public sealed record TrackerWidgetRow(
    string Id,
    string Title,
    string Details,
    TrackerPriority Priority);

/// <summary>
/// What a home-screen widget shows: one workspace's open items in list order. Platform-free, so the Android widget
/// only turns it into views.
/// </summary>
public sealed record TrackerWidgetModel(
    string? WorkspaceId,
    string Title,
    string Summary,
    string EmptyText,
    IReadOnlyList<TrackerWidgetRow> Rows,
    int OpenCount = 0)
{
    /// <summary>Nothing open: the widget shrinks to its small icon on a see-through background.</summary>
    public bool IsEmpty => OpenCount == 0;

    /// <summary>Widgets list at most this many items (a widget is a glance, not the whole list).</summary>
    public const int MaxRows = 50;

    /// <param name="workspaceId">The widget's workspace; the first workspace when null or deleted.</param>
    /// <param name="enabled">Whether the Tracker tool is turned on.</param>
    public static TrackerWidgetModel Build(TrackerStore store, string? workspaceId, bool enabled, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!enabled) return new TrackerWidgetModel(null, TrackerIds.DisplayName, "", "Tracker is turned off. Open Helm to turn it on.", []);

        // A new day: today's repeating tasks appear on the widget too.
        try { store.EnsureRepeats(); } catch (Exception) { /* drawing the widget must not fail on it */ }
        var workspaces = store.Workspaces();
        var chosen = workspaces.FirstOrDefault(w => w.Id == workspaceId) ?? workspaces.FirstOrDefault();
        if (chosen is null) return new TrackerWidgetModel(null, TrackerIds.DisplayName, "", "No workspaces yet. Tap + to create one in Helm.", []);

        var ws = chosen.Value;
        var open = store.OpenItems(chosen.Id);
        var rows = open.Take(MaxRows).Select(i => Row(i.Id, i.Value, now, zone)).ToList();
        return new TrackerWidgetModel(chosen.Id, ws.Name, $"{open.Count} open", "Nothing to do.", rows, open.Count);
    }

    /// <summary>The workspace after <paramref name="current"/> (wrapping), for the widget's "next workspace" tap.</summary>
    public static string? NextWorkspace(TrackerStore store, string? current)
    {
        var ids = store.Workspaces().Select(w => w.Id).ToList();
        if (ids.Count == 0) return null;
        var index = current is null ? -1 : ids.IndexOf(current);
        return ids[(index + 1) % ids.Count];
    }

    private static TrackerWidgetRow Row(string id, TrackerItem item, DateTimeOffset now, TimeZoneInfo zone)
    {
        var details = new List<string>();
        if (item.Priority is TrackerPriority.Urgent or TrackerPriority.High) details.Add(TrackerFormat.Priority(item.Priority));
        // The time given to Build, not the clock: the model is the same whenever it is drawn (and testable).
        if (item.DueAt is not null || item.DueDate is not null) details.Add(TrackerFormat.Due(item, now, zone));
        if (item.StartedExplicitly) details.Add("In progress");
        return new TrackerWidgetRow(id, item.Title, string.Join(" · ", details), item.Priority);
    }
}
