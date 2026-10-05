using System.Globalization;
using System.Text.Json;
using Helm.Core.Mcp;
using Helm.Core.Text;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tracker for Claude over MCP: the to-do lists (read, add, edit, tick, reopen); the debt book is Wallet's
/// (<c>wallet_debts</c>). Nothing is ever deleted through here. Dates are the user's local ones: "2026-10-01" or "2026-10-01T09:30".
/// </summary>
public sealed class TrackerMcpTools(TrackerStore store) : IMcpToolProvider
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private static readonly string[] Priorities = ["low", "normal", "high", "urgent"];

    public string? ModuleId => TrackerIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("tracker_lists", "The user's Tracker to-do lists, with how many tasks are open. Debts are in Wallet (wallet_debts).",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(Lists())) { ReadOnly = true },
        new("tracker_tasks", "Tasks from the to-do lists, with their subtasks. By default the open ones of every list.",
            McpArgs.Schema(
                ("list", McpArgs.Text("A list's id or name (default: every to-do list)."), false),
                ("status", McpArgs.OneOf("Which tasks (default open).", "open", "done", "all"), false),
                ("query", McpArgs.Text("Words to look for in titles and notes."), false),
                ("due_before", McpArgs.Text("Only tasks due before this day, e.g. 2026-10-01 (overdue and due today: tomorrow's date)."), false),
                ("limit", McpArgs.Number($"How many at most (default {DefaultLimit})."), false)),
            (args, _) => Task.FromResult<object?>(Tasks(args))) { ReadOnly = true },
        new("tracker_add_task", "Add a task to a to-do list, optionally with a due date or time, a priority, notes and subtasks.",
            McpArgs.Schema(
                ("title", McpArgs.Text("What to do."), true),
                ("list", McpArgs.Text("A list's id or name (default: the first to-do list)."), false),
                ("notes", McpArgs.Text("Details."), false),
                ("priority", McpArgs.OneOf("Default normal.", Priorities), false),
                ("due", McpArgs.Text("Due day or time in the user's time zone: 2026-10-01 or 2026-10-01T09:30."), false),
                ("subtasks", McpArgs.List("Titles of subtasks."), false)),
            (args, _) => Task.FromResult<object?>(AddTask(args))),
        new("tracker_update_task", "Change a task's title, notes, priority or due date.",
            McpArgs.Schema(
                ("id", McpArgs.Text("The task's id."), true),
                ("title", McpArgs.Text("New title."), false),
                ("notes", McpArgs.Text("New notes (replace the old ones)."), false),
                ("priority", McpArgs.OneOf("New priority.", Priorities), false),
                ("due", McpArgs.Text("New due day or time: 2026-10-01 or 2026-10-01T09:30."), false),
                ("clear_due", McpArgs.Flag("Remove the due date."), false)),
            (args, _) => Task.FromResult<object?>(UpdateTask(args))),
        new("tracker_complete_task", "Mark a task done (its finish time is recorded; its subtasks are done too).",
            McpArgs.Schema(("id", McpArgs.Text("The task's id."), true)),
            (args, _) => Task.FromResult<object?>(SetDone(McpArgs.RequiredString(args, "id"), done: true))),
        new("tracker_reopen_task", "Open a done task again.",
            McpArgs.Schema(("id", McpArgs.Text("The task's id."), true)),
            (args, _) => Task.FromResult<object?>(SetDone(McpArgs.RequiredString(args, "id"), done: false))),
    ];

    private object Lists() => store.Workspaces().Select(w => new
    {
        id = w.Id,
        name = w.Value.Name,
        kind = "tasks",
        open = store.OpenItems(w.Id).Count(i => !i.Value.IsSubtask),
    }).ToList();

    private object Tasks(JsonElement args)
    {
        var lists = TaskLists(McpArgs.String(args, "list"));
        var status = McpArgs.String(args, "status") ?? "open";
        if (status is not ("open" or "done" or "all")) throw new McpToolException("status is open, done or all.");
        var terms = TextSearch.Terms(McpArgs.String(args, "query"));
        var before = McpArgs.String(args, "due_before") is { } text ? ParseDay(text) : (DateOnly?)null;
        var limit = Math.Clamp(McpArgs.Int(args, "limit") ?? DefaultLimit, 1, MaxLimit);
        var zone = TimeZoneInfo.Local;
        return lists.SelectMany(ws => store.Items(ws.Id).Select(i => (ws, i)))
            .Where(x => !x.i.Value.IsSubtask)
            .Where(x => status == "all" || (status == "done") == x.i.Value.IsCompleted)
            .Where(x => terms.Count == 0 || TextSearch.Score(terms, x.i.Value.Title, x.i.Value.Notes) > 0)
            .Where(x => before is null || (x.i.Value.DueMoment(zone) is { } due && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(due, zone).DateTime) < before))
            .OrderBy(x => x.i.Value.IsCompleted)
            .ThenBy(x => x.i.Value.DueMoment(zone) ?? DateTimeOffset.MaxValue)
            .ThenByDescending(x => x.i.Value.Priority)
            .Take(limit)
            .Select(x => Describe(x.i.Id, x.i.Value, x.ws.Value.Name))
            .ToList();
    }

    private object Describe(string id, TrackerItem item, string list) => new
    {
        id,
        list,
        title = item.Title,
        notes = item.Notes.Length > 0 ? item.Notes : null,
        priority = Priorities[(int)item.Priority],
        due = Due(item),
        started = item.StartedExplicitly ? item.StartedAt : null,
        done = item.CompletedAt,
        repeats = item.RepeatDaily ? item.RepeatUntil is { } until ? $"every day until {until:yyyy-MM-dd}" : "every day" : null,
        subtasks = store.Subtasks(id).Select(s => new { id = s.Id, title = s.Value.Title, done = s.Value.IsCompleted }).ToList() is { Count: > 0 } subs ? subs : null,
    };

    private object AddTask(JsonElement args)
    {
        var title = McpArgs.RequiredString(args, "title").Trim();
        var list = TaskLists(McpArgs.String(args, "list")).FirstOrDefault() ?? throw new McpToolException("There is no to-do list yet; the user can create one in Helm.");
        var (day, at) = McpArgs.String(args, "due") is { } due ? ParseDue(due) : (null, null);
        var draft = new TrackerItemDraft(title, ParsePriority(McpArgs.String(args, "priority")) ?? TrackerPriority.Normal, day, McpArgs.String(args, "notes") ?? "", DueAt: at);
        var id = store.AddItem(list.Id, draft);
        foreach (var subtask in McpArgs.Strings(args, "subtasks").Where(s => s.Trim().Length > 0))
            store.AddItem(list.Id, new TrackerItemDraft(subtask.Trim(), ParentId: id));
        return Describe(id, store.GetItem(id)!, list.Value.Name);
    }

    private object UpdateTask(JsonElement args)
    {
        var id = McpArgs.RequiredString(args, "id");
        var item = RequireTask(id);
        var title = McpArgs.String(args, "title");
        var notes = McpArgs.String(args, "notes");
        var priority = ParsePriority(McpArgs.String(args, "priority"));
        var clear = McpArgs.Bool(args, "clear_due") == true;
        var due = McpArgs.String(args, "due") is { } text ? ParseDue(text) : ((DateOnly?, DateTimeOffset?)?)null;
        if (title is { Length: 0 }) throw new McpToolException("title cannot be empty.");
        store.UpdateItem(id, i => i with
        {
            Title = title?.Trim() ?? i.Title,
            Notes = notes ?? i.Notes,
            Priority = priority ?? i.Priority,
            DueDate = clear ? null : due is { } d ? d.Item1 : i.DueDate,
            DueAt = clear ? null : due is { } t ? t.Item2 : i.DueAt,
        });
        return Describe(id, store.GetItem(id)!, store.GetWorkspace(item.WorkspaceId)?.Name ?? "");
    }

    private object SetDone(string id, bool done)
    {
        var item = RequireTask(id);
        if (item.IsCompleted != done)
        {
            if (done) store.Complete(id);
            else store.Reopen(id);
        }
        return Describe(id, store.GetItem(id)!, store.GetWorkspace(item.WorkspaceId)?.Name ?? "");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private List<Helm.Core.Sync.SyncedItem<TrackerWorkspace>> TaskLists(string? list)
    {
        var lists = store.Workspaces().Where(w => w.Value.Kind == WorkspaceKind.Tasks).ToList();
        if (string.IsNullOrWhiteSpace(list)) return lists;
        var match = lists.Where(w => w.Id == list || string.Equals(w.Value.Name.Trim(), list.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        return match.Count > 0 ? match : throw new McpToolException($"There is no to-do list {list}. Lists: {string.Join(", ", lists.Select(l => l.Value.Name))}.");
    }

    private TrackerItem RequireTask(string id)
    {
        var item = store.GetItem(id) ?? throw new McpToolException($"There is no task {id}.");
        if (store.GetWorkspace(item.WorkspaceId)?.Kind != WorkspaceKind.Tasks) throw new McpToolException($"There is no task {id}.");
        return item;
    }

    private static TrackerPriority? ParsePriority(string? text) => text switch
    {
        null => null,
        _ when Array.IndexOf(Priorities, text.Trim().ToLowerInvariant()) is var i and >= 0 => (TrackerPriority)i,
        _ => throw new McpToolException($"priority is {string.Join(", ", Priorities)}."),
    };

    private static DateOnly ParseDay(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day : throw new McpToolException($"{text} is not a day like 2026-10-01.");

    /// <summary>"2026-10-01" (due that day) or "2026-10-01T09:30" (at that local time).</summary>
    private static (DateOnly? Day, DateTimeOffset? At) ParseDue(string text)
    {
        text = text.Trim();
        if (text.Length == 10) return (ParseDay(text), null);
        if (!DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new McpToolException($"{text} is not a day or a time like 2026-10-01 or 2026-10-01T09:30.");
        var at = TrackerDue.FromLocal(local.Date, local.TimeOfDay, TimeZoneInfo.Local);
        return (DateOnly.FromDateTime(local), at);
    }

    private static string? Due(TrackerItem item) =>
        item.DueAt is { } at ? TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)
        : item.DueDate is { } day ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
}
