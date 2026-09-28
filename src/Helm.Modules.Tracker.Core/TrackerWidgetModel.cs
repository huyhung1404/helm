namespace Helm.Modules.Tracker;

public sealed record TrackerWidgetRow(
    string Id,
    string Title,
    string Details,
    string Amount,
    TrackerPriority Priority,
    bool IsDebt,
    bool OwedToMe)
{
    /// <summary>Tasks are ticked off from the widget; a debt is settled by repaying it, in the app.</summary>
    public bool CanComplete => !IsDebt;
}

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

        var workspaces = store.Workspaces();
        var chosen = workspaces.FirstOrDefault(w => w.Id == workspaceId) ?? workspaces.FirstOrDefault();
        if (chosen is null) return new TrackerWidgetModel(null, TrackerIds.DisplayName, "", "No workspaces yet. Tap + to create one in Helm.", []);

        var ws = chosen.Value;
        var debts = ws.Kind == WorkspaceKind.Debts;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        if (debts)
        {
            // One row per person with a balance, as in the app.
            var people = DebtLedger.Open(store.Items(chosen.Id)).Where(p => !p.IsSettled).ToList();
            var personRows = people.Take(MaxRows).Select(p => new TrackerWidgetRow(
                "person:" + p.Key,
                p.Name,
                p.Due(zone) is { } due ? TrackerFormat.Due(new TrackerItem { DueAt = due }, now, zone) : (p.Entries.Count == 1 ? "1 entry" : $"{p.Entries.Count} entries"),
                TrackerFormat.Balance(p.Balance, ws.Currency),
                TrackerPriority.Normal,
                IsDebt: true,
                OwedToMe: p.Balance > 0)).ToList();
            var net = people.Sum(p => p.Balance);
            var debtSummary = people.Count == 0 ? "Everyone is square" : TrackerFormat.Balance(net, ws.Currency);
            return new TrackerWidgetModel(chosen.Id, ws.Name, debtSummary, "Nothing outstanding.", personRows, people.Count);
        }

        var open = store.OpenItems(chosen.Id);
        var rows = open.Take(MaxRows).Select(i => Row(i.Id, i.Value, ws, today)).ToList();
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

    private static TrackerWidgetRow Row(string id, TrackerItem item, TrackerWorkspace ws, DateOnly today)
    {
        var debt = ws.Kind == WorkspaceKind.Debts;
        string title;
        if (debt && item.Person.Length > 0) title = item.Title.Length > 0 ? $"{item.Person} — {item.Title}" : item.Person;
        else title = item.Title;

        var details = new List<string>();
        if (!debt && item.Priority is TrackerPriority.Urgent or TrackerPriority.High) details.Add(TrackerFormat.Priority(item.Priority));
        if (item.DueAt is not null || item.DueDate is not null) details.Add(TrackerFormat.Due(item, DateTimeOffset.Now));
        if (!debt && item.StartedExplicitly) details.Add("In progress");

        var amount = debt
            ? (item.Direction == DebtDirection.TheyOweMe ? "+" : "−") + TrackerFormat.Money(item.Amount, ws.Currency)
            : "";
        return new TrackerWidgetRow(id, title, string.Join(" · ", details), amount, item.Priority, debt, item.Direction == DebtDirection.TheyOweMe);
    }
}
