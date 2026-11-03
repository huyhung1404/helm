using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Core.Text;

namespace Helm.Modules.Tracker;

/// <summary>
/// Tracker for AI agents over MCP: the to-do lists (read, add lists and tasks, edit, start, tick, reopen); the debt book is Wallet's
/// (<c>wallet_debts</c>). Nothing is ever deleted through here. Dates are the user's local ones: "2026-10-01" or "2026-10-01T09:30".
/// </summary>
public sealed class TrackerMcpTools(TrackerStore store) : IMcpToolProvider
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private static readonly string[] Priorities = ["low", "normal", "high", "urgent"];

    private static readonly JsonSerializerOptions Unchanged = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>At most this many tasks in one tracker_add_tasks call.</summary>
    internal const int MaxBatch = 30;

    public string? ModuleId => TrackerIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("tracker_lists", "The user's Tracker to-do lists, with how many tasks are open. Debts are in Wallet (wallet_debts).",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(Lists()))
        {
            ReadOnly = true,
            Brief = "The to-do lists, with how many tasks are open.",
            BriefResult = McpTool.Drop("kind"),
        },
        new("tracker_tasks", "Tasks from the to-do lists, with their subtasks. By default the open ones of every list.",
            McpArgs.Schema(
                ("list", McpArgs.Text("A list's id or name (default: every to-do list)."), false),
                ("status", McpArgs.OneOf("Which tasks (default open).", "open", "done", "all"), false),
                ("query", McpArgs.Text("Words to look for in titles and notes."), false),
                ("due_before", McpArgs.Text("Only tasks due before this day, e.g. 2026-10-01 (overdue and due today: tomorrow's date)."), false),
                ("limit", McpArgs.Number($"How many at most (default {DefaultLimit})."), false)),
            (args, _) => Task.FromResult<object?>(Tasks(args)))
        {
            ReadOnly = true,
            Brief = "Tasks with their subtasks; default the open ones of every list.",
            BriefArgs = new Dictionary<string, string>
            {
                ["list"] = "id or name",
                ["query"] = "words in titles and notes",
                ["due_before"] = "2026-10-01 (tomorrow: overdue and today)",
            },
        },
        new("tracker_add_task",
            "Add a task to a to-do list, optionally with a due date or time, a priority, notes, subtasks, or repeating every day. " +
            "With parent, the task is added as a subtask of that task instead.",
            McpArgs.Schema(
                ("title", McpArgs.Text("What to do."), true),
                ("list", McpArgs.Text("A list's id or name (default: the first to-do list)."), false),
                ("parent", McpArgs.Text("A task's id: add this as one of its subtasks (in the task's list)."), false),
                ("notes", McpArgs.Text("Details."), false),
                ("priority", McpArgs.OneOf("Default normal.", Priorities), false),
                ("due", McpArgs.Text("Due day or time in the user's time zone: 2026-10-01 or 2026-10-01T09:30."), false),
                ("subtasks", McpArgs.List("Titles of subtasks."), false),
                ("repeat_daily", McpArgs.Flag("A new copy of the task appears every day, starting today."), false),
                ("repeat_until", McpArgs.Text("With repeat_daily: the last day, e.g. 2026-12-31 (default: no end)."), false)),
            (args, _) => Task.FromResult<object?>(AddTask(args)))
        {
            Undo = (result, _) => { McpTool.Undone(store.DeleteItem(result.GetProperty("id").GetString()!)); return Task.CompletedTask; },
            Brief = "Add a task (with parent: a subtask of that task), optionally due, with a priority, notes, subtasks or repeating every day.",
            BriefArgs = new Dictionary<string, string>
            {
                ["list"] = "id or name; default the first list",
                ["parent"] = "a task's id",
                ["due"] = "local: 2026-10-01 or 2026-10-01T09:30",
                ["repeat_until"] = "with repeat_daily: the last day",
            },
            BriefResult = McpTool.Keep("id", "list", "title", "due", "repeats", "subtasks"),
        },
        new("tracker_add_tasks",
            "Add several tasks in one call, to a list by name (made when the user has none by that name), or as subtasks of one task " +
            "(parent): the action items of a note (read it with notes_read first; only what is to be done, with a due date only when " +
            "the note says one), or the steps of a big task. To break a task into steps, first propose 3-8 short steps in your " +
            "answer and ask which to keep; add only the ones the user keeps.",
            McpArgs.Schema(
                ("tasks", new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["title"] = McpArgs.Text("What to do, short, starting with a verb."),
                            ["notes"] = McpArgs.Text("Details (who, where), optional."),
                            ["due"] = McpArgs.Text("Due day or time, optional: 2026-10-01 or 2026-10-01T09:30."),
                        },
                        ["required"] = new JsonArray("title"),
                    },
                    ["description"] = $"The tasks, in order (at most {MaxBatch}).",
                }, true),
                ("list", McpArgs.Text("A list's id or name; a name the user has no list for makes that list (default: the first to-do list)."), false),
                ("parent", McpArgs.Text("A task's id: add them as its subtasks instead (in its list)."), false)),
            (args, _) => Task.FromResult<object?>(AddTasks(args)))
        {
            Brief = "Add several tasks in one call, to a list by name (made when there is none) or as subtasks of parent: a note's action items " +
                "(notes_read it first; a due date only when it says one) or a big task's steps (first propose 3-8 short steps and ask which to keep).",
            BriefArgs = new Dictionary<string, string>
            {
                ["tasks"] = $"in order, at most {MaxBatch}; title starts with a verb; due as tracker_add_task's",
                ["list"] = "id or name; a new name makes the list",
                ["parent"] = "a task's id",
            },
            BriefResult = McpTool.Drop("ids", "list_id", "priority"),
            // The tasks this call added, and the list when it made one.
            Undo = (result, _) =>
            {
                var any = false;
                foreach (var id in result.GetProperty("ids").EnumerateArray()) any |= store.DeleteItem(id.GetString()!);
                if (result.TryGetProperty("created_list", out var made) && made.GetBoolean()) any |= store.DeleteWorkspace(result.GetProperty("list_id").GetString()!);
                McpTool.Undone(any);
                return Task.CompletedTask;
            },
        },
        new("tracker_update_task", "Change a task's title, notes, priority or due date, or stop it repeating.",
            McpArgs.Schema(
                ("id", McpArgs.Text("The task's id."), true),
                ("title", McpArgs.Text("New title."), false),
                ("notes", McpArgs.Text("New notes (replace the old ones)."), false),
                ("priority", McpArgs.OneOf("New priority.", Priorities), false),
                ("due", McpArgs.Text("New due day or time: 2026-10-01 or 2026-10-01T09:30."), false),
                ("clear_due", McpArgs.Flag("Remove the due date."), false),
                ("stop_repeating", McpArgs.Flag("A repeating task: no new day appears (the days already there stay)."), false)),
            (args, _) => Task.FromResult<object?>(UpdateTask(args))),
        new("tracker_start_task", "Record that work on a task (or subtask) started now; its time to finish is then counted from here.",
            McpArgs.Schema(("id", McpArgs.Text("The task's id."), true)),
            (args, _) => Task.FromResult<object?>(StartTask(McpArgs.RequiredString(args, "id"))))
        {
            Brief = "Record that work on a task started now.",
            BriefResult = McpTool.Keep("id", "title", "started"),
        },
        new("tracker_complete_task", "Mark a task or subtask done (its finish time is recorded; a task's subtasks are done too).",
            McpArgs.Schema(("id", McpArgs.Text("The task's id."), true)),
            (args, _) => Task.FromResult<object?>(SetDone(McpArgs.RequiredString(args, "id"), done: true)))
        {
            Brief = "Mark a task or subtask done (with its subtasks).",
            BriefResult = McpTool.Keep("id", "title", "done"),
            // Only a task this call ticked: one that was done before stays done (such a result is "unchanged").
            Undo = (result, _) =>
            {
                if (!McpTool.ChangedNothing(result)) McpTool.Undone(store.Reopen(result.GetProperty("id").GetString()!));
                return Task.CompletedTask;
            },
        },
        new("tracker_reopen_task", "Open a done task or subtask again.",
            McpArgs.Schema(("id", McpArgs.Text("The task's id."), true)),
            (args, _) => Task.FromResult<object?>(SetDone(McpArgs.RequiredString(args, "id"), done: false))),
        new("tracker_add_list", "Add a to-do list (for tasks; debts are in Wallet).",
            McpArgs.Schema(("name", McpArgs.Text("The list's name."), true)),
            (args, _) => Task.FromResult<object?>(AddList(McpArgs.RequiredString(args, "name")))),
    ];

    private object AddList(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new McpToolException("name is required.");
        if (store.Workspaces().FirstOrDefault(w => string.Equals(w.Value.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase)) is { } same)
            throw new McpToolException($"There is already a list {same.Value.Name} ({same.Id}).");
        var id = store.AddWorkspace(name);
        return new { id, name, kind = "tasks", open = 0 };
    }

    private object StartTask(string id)
    {
        var item = RequireTask(id);
        if (item.IsCompleted) throw new McpToolException($"“{item.Title}” is done; reopen it first.");
        store.Start(id);
        return Describe(id, store.GetItem(id)!, store.GetWorkspace(item.WorkspaceId)?.Name ?? "");
    }

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
        var parentId = McpArgs.String(args, "parent");
        var parent = parentId is null ? null : RequireTask(parentId);
        if (parent is { IsSubtask: true }) throw new McpToolException("A subtask cannot have subtasks; add it to its task instead.");
        var list = parent is not null
            ? store.Workspaces().First(w => w.Id == parent.WorkspaceId)
            : TaskLists(McpArgs.String(args, "list")).FirstOrDefault() ?? throw new McpToolException("There is no to-do list yet; add one with tracker_add_list.");
        var (day, at) = McpArgs.String(args, "due") is { } due ? ParseDue(due) : (null, null);
        var repeat = McpArgs.Bool(args, "repeat_daily") == true;
        var until = McpArgs.String(args, "repeat_until") is { } last ? ParseDay(last) : (DateOnly?)null;
        if (until is not null && !repeat) throw new McpToolException("repeat_until goes with repeat_daily.");
        if (repeat && parent is not null) throw new McpToolException("A subtask cannot repeat; its task can.");
        var draft = new TrackerItemDraft(title, ParsePriority(McpArgs.String(args, "priority")) ?? TrackerPriority.Normal, day, McpArgs.String(args, "notes") ?? "",
            DueAt: at, ParentId: parentId, RepeatDaily: repeat, RepeatUntil: until);
        var id = store.AddItem(list.Id, draft);
        foreach (var subtask in McpArgs.Strings(args, "subtasks").Where(s => s.Trim().Length > 0))
            store.AddItem(list.Id, new TrackerItemDraft(subtask.Trim(), ParentId: id));
        return Describe(id, store.GetItem(id)!, list.Value.Name);
    }

    private object AddTasks(JsonElement args)
    {
        if (!args.TryGetProperty("tasks", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new McpToolException("tasks must be a list of { title, notes, due }.");
        var tasks = list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object)
            .Select(t => new ProposedTask(McpArgs.String(t, "title")?.Trim() ?? "", McpArgs.String(t, "notes")?.Trim() ?? "", McpArgs.String(t, "due")))
            .Where(t => t.Title.Length > 0)
            .ToList();
        if (tasks.Count == 0) throw new McpToolException("Give at least one task with a title.");
        if (tasks.Count > MaxBatch) throw new McpToolException($"At most {MaxBatch} tasks in one call.");
        foreach (var due in tasks.Select(t => t.Due).OfType<string>()) ParseDue(due);

        var parentId = McpArgs.String(args, "parent");
        var parent = parentId is null ? null : RequireTask(parentId);
        if (parent is { IsSubtask: true }) throw new McpToolException("A subtask cannot have subtasks; add them to its task instead.");
        string listId;
        var created = false;
        if (parent is not null) listId = parent.WorkspaceId;
        else if (McpArgs.String(args, "list") is { } name && name.Trim().Length > 0 && !TaskListExists(name))
        {
            listId = store.AddWorkspace(name.Trim());
            created = true;
        }
        else listId = TaskLists(McpArgs.String(args, "list")).FirstOrDefault()?.Id ?? throw new McpToolException("There is no to-do list yet; name one.");
        var ids = TaskProposals.AddAll(store, listId, parentId, tasks);
        var listName = store.GetWorkspace(listId)?.Name ?? "";
        return new { ids, list_id = listId, list = listName, created_list = created, tasks = ids.Select(id => Describe(id, store.GetItem(id)!, listName)).ToList() };
    }

    private bool TaskListExists(string list) => store.Workspaces().Any(w => w.Value.Kind == WorkspaceKind.Tasks
        && (w.Id == list || string.Equals(w.Value.Name.Trim(), list.Trim(), StringComparison.CurrentCultureIgnoreCase)));

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
        if (McpArgs.Bool(args, "stop_repeating") == true && store.GetItem(id) is { SeriesId: { } series }) store.StopRepeating(series);
        return Describe(id, store.GetItem(id)!, store.GetWorkspace(item.WorkspaceId)?.Name ?? "");
    }

    /// <summary>The task as it is now; with <c>unchanged: true</c> when it already was so (nothing for Undo to take back).</summary>
    private object SetDone(string id, bool done)
    {
        var item = RequireTask(id);
        var changed = item.IsCompleted != done;
        if (changed)
        {
            if (done) store.Complete(id);
            else store.Reopen(id);
        }
        var task = Describe(id, store.GetItem(id)!, store.GetWorkspace(item.WorkspaceId)?.Name ?? "");
        if (changed) return task;
        var node = JsonSerializer.SerializeToNode(task, Unchanged)!.AsObject();
        node[McpTool.UnchangedProperty] = true;
        return node;
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
    internal static (DateOnly? Day, DateTimeOffset? At) ParseDue(string text)
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
