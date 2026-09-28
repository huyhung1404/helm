namespace Helm.Modules.Tracker;

public sealed record TrackerWidgetRow(
    string Id,
    string Title,
    string Details,
    string Amount,
    TrackerPriority Priority,
    bool IsDebt,
    bool OwedToMe);

/// <summary>
/// What a home-screen widget shows: one workspace's open items in list order. Platform-free, so the Android widget
/// only turns it into views.
/// </summary>
public sealed record TrackerWidgetModel(
    string? WorkspaceId,
    string Title,
    string Summary,
    string EmptyText,
    IReadOnlyList<TrackerWidgetRow> Rows)
{
    /// <summary>Widgets list at most this many items (a widget is a glance, not the whole list).</summary>
    public const int MaxRows = 50;

    /// <param name="workspaceId">The widget's workspace; the first workspace when null or deleted.</param>
    /// <param name="enabled">Whether the Tracker tool is turned on.</param>
    public static TrackerWidgetModel Build(TrackerStore store, string? workspaceId, bool enabled, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!enabled) return new TrackerWidgetModel(null, TrackerIds.DisplayName, "", "Tracker is turned off. Open Helm to turn it on.", []);

        var workspaces = store.Workspaces();
        var chosen = workspaces.FirstOrDefault(w => w.Id == workspaceId) ?? workspaces.FirstOrDefault();
        if (chosen is null) return new TrackerWidgetModel(null, TrackerIds.DisplayName, "", "No workspaces yet. Tap + to create one in Helm.", []);

        var ws = chosen.Value;
        var debts = ws.Kind == WorkspaceKind.Debts;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var open = store.OpenItems(chosen.Id);
        var rows = open.Take(MaxRows).Select(i => Row(i.Id, i.Value, ws, today)).ToList();

        string summary;
        if (debts)
        {
            var totals = DebtTotals.From(open.Select(i => i.Value));
            summary = totals.Net == 0 ? $"{open.Count} open"
                : (totals.Net > 0 ? "+" : "−") + TrackerFormat.Money(Math.Abs(totals.Net), ws.Currency);
        }
        else
        {
            summary = $"{open.Count} open";
        }
        var empty = debts ? "Nothing outstanding." : "Nothing to do.";
        return new TrackerWidgetModel(chosen.Id, ws.Name, summary, empty, rows);
    }

    /// <summary>The workspace after <paramref name="current"/> (wrapping), for the widget's "next workspace" tap.</summary>
    public static string? NextWorkspace(TrackerStore store, string? current)
    {
        var ids = store.Workspaces().Select(w => w.Id).ToList();
        if (ids.Count == 0) return null;
        var index = current is null ? -1 : ids.IndexOf(current);
        return ids[(index + 1) % ids.Count];
    }

    private static TrackerWidgetRow Row(string id, TrackerItem item, TrackerWorkspace ws, DateOnly today)
    {
        var debt = ws.Kind == WorkspaceKind.Debts;
        string title;
        if (debt && item.Person.Length > 0) title = item.Title.Length > 0 ? $"{item.Person} — {item.Title}" : item.Person;
        else title = item.Title;

        var details = new List<string>();
        if (!debt && item.Priority is TrackerPriority.Urgent or TrackerPriority.High) details.Add(TrackerFormat.Priority(item.Priority));
        if (item.DueDate is { } due) details.Add(TrackerFormat.Due(due, today));
        if (!debt && item.StartedExplicitly) details.Add("In progress");

        var amount = debt
            ? (item.Direction == DebtDirection.TheyOweMe ? "+" : "−") + TrackerFormat.Money(item.Amount, ws.Currency)
            : "";
        return new TrackerWidgetRow(id, title, string.Join(" · ", details), amount, item.Priority, debt, item.Direction == DebtDirection.TheyOweMe);
    }
}
