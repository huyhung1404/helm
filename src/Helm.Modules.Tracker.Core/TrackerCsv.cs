using System.Globalization;
using System.Text;
using Helm.Core.Sync;

namespace Helm.Modules.Tracker;

/// <summary>
/// CSV exports for analysis in a spreadsheet: RFC 4180 quoting, ISO 8601 times with offset, invariant numbers, and
/// durations in minutes so they can be summed and averaged directly.
/// </summary>
public static class TrackerCsv
{
    private static readonly string[] ItemHeader =
    [
        "workspace", "kind", "title", "priority", "status", "created", "started", "completed", "due",
        "lead_minutes", "work_minutes", "person", "amount", "direction", "notes", "id",
    ];

    private static readonly string[] HistoryHeader =
    [
        "event", "at", "workspace", "kind", "title", "priority", "created", "started", "completed", "due",
        "lead_minutes", "work_minutes", "person", "amount", "direction", "item_id",
    ];

    public static string Items(IEnumerable<SyncedItem<TrackerItem>> items, IReadOnlyDictionary<string, TrackerWorkspace> workspaces)
    {
        var sb = new StringBuilder();
        Row(sb, ItemHeader);
        foreach (var (id, item, _) in items.OrderBy(i => i.Value.CreatedAt))
        {
            var workspace = workspaces.GetValueOrDefault(item.WorkspaceId);
            Row(sb,
            [
                workspace?.Name ?? "",
                (workspace?.Kind ?? WorkspaceKind.Tasks).ToString(),
                item.Title,
                item.Priority.ToString(),
                item.IsCompleted ? "Completed" : item.StartedAt is not null ? "Started" : "Open",
                Time(item.CreatedAt),
                Time(item.StartedAt),
                Time(item.CompletedAt),
                item.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                Minutes(item.CompletedAt - item.CreatedAt),
                Minutes(item.StartedExplicitly ? item.CompletedAt - item.StartedAt : null),
                item.Person,
                workspace?.Kind == WorkspaceKind.Debts ? item.Amount.ToString(CultureInfo.InvariantCulture) : "",
                workspace?.Kind == WorkspaceKind.Debts ? item.Direction.ToString() : "",
                item.Notes,
                id,
            ]);
        }
        return sb.ToString();
    }

    public static string History(IEnumerable<TrackerEvent> events, IReadOnlyDictionary<string, TrackerWorkspace> workspaces)
    {
        var sb = new StringBuilder();
        Row(sb, HistoryHeader);
        foreach (var e in events.OrderBy(e => e.At))
        {
            var debts = e.WorkspaceKind == WorkspaceKind.Debts;
            Row(sb,
            [
                e.Kind.ToString(),
                Time(e.At),
                workspaces.GetValueOrDefault(e.WorkspaceId)?.Name ?? "",
                e.WorkspaceKind.ToString(),
                e.Title,
                e.Priority.ToString(),
                Time(e.CreatedAt),
                Time(e.StartedAt),
                Time(e.CompletedAt),
                e.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                e.Kind == TrackerEventKind.Completed ? Minutes(e.CompletedAt - e.CreatedAt) : "",
                e.Kind == TrackerEventKind.Completed && e.StartedExplicitly ? Minutes(e.CompletedAt - e.StartedAt) : "",
                e.Person,
                debts ? e.Amount.ToString(CultureInfo.InvariantCulture) : "",
                debts ? e.Direction.ToString() : "",
                e.ItemId,
            ]);
        }
        return sb.ToString();
    }

    internal static string Escape(string value)
    {
        // Leading =, +, - or @ would run as a formula in Excel; prefix it so text stays text.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static void Row(StringBuilder sb, IEnumerable<string> cells)
    {
        sb.AppendJoin(',', cells.Select(Escape));
        sb.Append("\r\n");
    }

    /// <summary>Local time with its offset, so a spreadsheet shows the hour the user saw.</summary>
    private static string Time(DateTimeOffset? at) => at?.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) ?? "";

    private static string Minutes(TimeSpan? span) => span is { } s ? Math.Round(s.TotalMinutes, 1).ToString(CultureInfo.InvariantCulture) : "";
}
