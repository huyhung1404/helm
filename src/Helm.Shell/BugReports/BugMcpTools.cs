using System.Globalization;
using System.Text.Json;
using Helm.Core.Diagnostics;
using Helm.Core.Mcp;
using Helm.Core.Sync;

namespace Helm.Shell.BugReports;

/// <summary>
/// Bug reports for AI agents over MCP: read the user's bugs with their black boxes and pictures, take one, write how it
/// goes, ask the user, and hand it back fixed. An agent never closes a bug: <c>bug_resolve</c> moves it to "to verify"
/// and the user closes it after checking. Always offered (bug reports are part of Helm, not a tool).
/// </summary>
public sealed class BugMcpTools(BugStore store, Func<string>? imageFolder = null) : IMcpToolProvider
{
    private const int DefaultLimit = 30;
    private const int MaxLimit = 200;
    private const int MaxStack = 2500;

    private static readonly string[] Statuses = ["not_closed", "open", "fixing", "to_verify", "closed", "all"];
    private static readonly string[] Kinds = ["input", "navigation", "log", "error", "performance", "sync", "disabled_click"];

    public string? ModuleId => null;

    public IEnumerable<McpTool> Tools =>
    [
        new("bugs_list", "The bugs the user reported in Helm (on their PC or phone), newest first. Default: the ones not closed. Each has a code like B12 for the other bug_ tools.",
            McpArgs.Schema(
                ("status", McpArgs.OneOf("Which bugs (default not_closed).", Statuses), false),
                ("query", McpArgs.Text("Words to look for in titles and descriptions."), false),
                ("tool", McpArgs.Text("Only the bugs of this tool (\"Tracker\")."), false),
                ("limit", McpArgs.Number($"How many at most (default {DefaultLimit})."), false)),
            (args, _) => Task.FromResult<object?>(List(args)))
        {
            ReadOnly = true,
            Brief = "The user's Helm bug reports, newest first; default the ones not closed.",
        },
        new("bug_get",
            "One bug: what the user wrote, where (tool, device, Helm version), its status, the whole conversation, and its black box summed up: " +
            "the steps the user took in the 60 s before reporting, the errors with their stacks, UI freezes, clicks on disabled buttons, and Helm's state. " +
            "Start here to fix a bug; then bug_blackbox for every event and bug_images for the pictures. To work on it, call bug_claim first.",
            McpArgs.Schema(("bug", McpArgs.Text("The bug: B12, #B12 or 12."), true)),
            (args, ct) => Get(args, ct))
        {
            ReadOnly = true,
            Brief = "One bug with its conversation and black box summed up.",
        },
        new("bug_blackbox",
            "Every event of a bug's black box (the last 60 s before the report, or before details the user added later): input, navigation, Helm's log, errors with full stacks, UI freezes, sync. " +
            "Times are seconds before the report. The user's typing is never recorded, only how much they typed.",
            McpArgs.Schema(
                ("bug", McpArgs.Text("The bug: B12."), true),
                ("entry", McpArgs.Text("Which black box: an entry id from bug_get (default the newest)."), false),
                ("kinds", McpArgs.List("Only these kinds: " + string.Join(", ", Kinds) + "."), false)),
            (args, ct) => BlackBoxOf(args, ct))
        {
            ReadOnly = true,
            Brief = "Every event of a bug's black box.",
        },
        new("bug_images",
            "The pictures the user attached to a bug (screenshots they chose), as images, and also saved as files on this PC whose paths are listed.",
            McpArgs.Schema(
                ("bug", McpArgs.Text("The bug: B12."), true),
                ("entry", McpArgs.Text("Only the pictures of this entry (default all of them)."), false)),
            (args, ct) => Images(args, ct))
        {
            ReadOnly = true,
            Brief = "The pictures attached to a bug.",
        },
        new("bug_claim",
            "Take a bug to fix it: it shows as \"Fixing\" with your name on the user's devices. Refused when it is closed, waiting for the user to check, or another agent has it.",
            McpArgs.Schema(
                ("bug", McpArgs.Text("The bug: B12."), true),
                ("project", McpArgs.Text("The project or folder you work in (\"helm\")."), false),
                ("branch", McpArgs.Text("The branch of the fix, e.g. fix/bug-B12-tracker-save."), false)),
            (args, _) => Task.FromResult<object?>(Changed(store.Claim(BugId(args), Agent(args), McpArgs.String(args, "branch")))))
        {
            Undo = UndoChange,
            Brief = "Take a bug to fix it.",
            BriefResult = McpTool.Keep("bug", "status", "summary"),
        },
        new("bug_comment", "Write on a bug how the fix is going (what you found, what you changed). The user reads it on their devices.",
            McpArgs.Schema(
                ("bug", McpArgs.Text("The bug: B12."), true),
                ("text", McpArgs.Text("The note, short, in the user's language."), true),
                ("project", McpArgs.Text("The project you work in (\"helm\")."), false)),
            (args, _) => Task.FromResult<object?>(Changed(store.Note(BugId(args), Agent(args), McpArgs.RequiredString(args, "text")))))
        {
            Undo = UndoChange,
            Brief = "Write how the fix is going.",
            BriefResult = McpTool.Keep("bug", "summary"),
        },
        new("bug_ask",
            "Ask the user something about a bug (how to make it happen, what they expected). Their phone tells them; read the answer later with bug_get. Ask one clear question.",
            McpArgs.Schema(
                ("bug", McpArgs.Text("The bug: B12."), true),
                ("question", McpArgs.Text("The question, in the user's language."), true),
                ("project", McpArgs.Text("The project you work in (\"helm\")."), false)),
            (args, _) => Task.FromResult<object?>(Changed(store.Ask(BugId(args), Agent(args), McpArgs.RequiredString(args, "question")))))
        {
            Undo = UndoChange,
            Brief = "Ask the user about a bug.",
            BriefResult = McpTool.Keep("bug", "summary"),
        },
        new("bug_resolve",
            "Say a bug is fixed: the cause and what changed, and the branch or the Helm version that has it. It moves to \"To verify\"; the user closes it after checking (you cannot). " +
            "When a version is given, Helm asks the user once their device runs it.",
            McpArgs.Schema(
                ("bug", McpArgs.Text("The bug: B12."), true),
                ("resolution", McpArgs.Text("The cause and the fix, short, in the user's language."), true),
                ("fixed_in", McpArgs.Text("The Helm version with the fix, e.g. 0.86.0 (when you know it)."), false),
                ("branch", McpArgs.Text("The branch of the fix."), false),
                ("project", McpArgs.Text("The project you work in (\"helm\")."), false)),
            (args, _) => Task.FromResult<object?>(Changed(store.Resolve(BugId(args), Agent(args), McpArgs.RequiredString(args, "resolution"),
                McpArgs.String(args, "fixed_in"), McpArgs.String(args, "branch")))))
        {
            Undo = UndoChange,
            Brief = "A bug is fixed: it waits for the user to check.",
            BriefResult = McpTool.Keep("bug", "status", "summary"),
        },
        new("bug_release", "Give a bug back when you cannot fix it, with why. It goes back to \"Open\".",
            McpArgs.Schema(
                ("bug", McpArgs.Text("The bug: B12."), true),
                ("reason", McpArgs.Text("Why, in the user's language."), true),
                ("project", McpArgs.Text("The project you work in (\"helm\")."), false)),
            (args, _) => Task.FromResult<object?>(Changed(store.Release(BugId(args), Agent(args), McpArgs.RequiredString(args, "reason")))))
        {
            Undo = UndoChange,
            Brief = "Give a bug back.",
            BriefResult = McpTool.Keep("bug", "status", "summary"),
        },
    ];

    // ---- reading ----

    private object List(JsonElement args)
    {
        var status = McpArgs.String(args, "status") ?? "not_closed";
        if (!Statuses.Contains(status)) throw new ArgumentException($"status is one of {string.Join(", ", Statuses)}.");
        var query = McpArgs.String(args, "query");
        var tool = McpArgs.String(args, "tool");
        var limit = Math.Clamp(McpArgs.Int(args, "limit") ?? DefaultLimit, 1, MaxLimit);
        var bugs = store.All().Where(b => status switch
        {
            "not_closed" => b.Value.Status != BugStatus.Closed,
            "all" => true,
            _ => StatusName(b.Value.Status) == status,
        });
        if (!string.IsNullOrWhiteSpace(tool)) bugs = bugs.Where(b => b.Value.Tool.Equals(tool.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query))
            bugs = bugs.Where(b => b.Value.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || b.Value.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        var list = bugs.ToList();
        return new
        {
            count = list.Count,
            bugs = list.Take(limit).Select(b => new
            {
                bug = b.Value.Code,
                title = b.Value.Title,
                status = StatusName(b.Value.Status),
                severity = SeverityName(b.Value.Severity),
                tool = b.Value.Tool,
                device = b.Value.Device,
                reported = Local(b.Value.CreatedAt),
                version = b.Value.AppVersion,
                fixing_by = b.Value.FixingBy,
                fixed_in = b.Value.FixedIn,
                waiting_for_answer = b.Value.PendingQuestion is not null ? true : (bool?)null,
            }),
        };
    }

    private async Task<object?> Get(JsonElement args, CancellationToken ct)
    {
        var (id, bug) = Bug(args);
        var entries = store.Entries(id);
        var latest = store.LatestBlackBox(id);
        var box = latest is null ? null : await store.ReadBlackBoxAsync(latest.Value, ct).ConfigureAwait(false);
        return new
        {
            bug = bug.Code,
            title = bug.Title,
            description = bug.Description,
            status = StatusName(bug.Status),
            severity = SeverityName(bug.Severity),
            tool = bug.Tool,
            device = $"{bug.Device} ({(bug.DeviceKind == BugDeviceKind.Pc ? "Windows PC" : "Android phone")})",
            version = bug.AppVersion,
            reported = Local(bug.CreatedAt),
            from_crash = bug.FromCrash ? true : (bool?)null,
            fixing_by = bug.FixingBy,
            branch = bug.Branch,
            fixed_in = bug.FixedIn,
            resolution = bug.Resolution,
            close_reason = bug.CloseReason?.ToString(),
            waiting_for_answer = bug.PendingQuestion is not null ? true : (bool?)null,
            conversation = entries.Select(e => new
            {
                entry = e.Id,
                kind = e.Value.Kind.ToString(),
                by = e.Value.Author,
                on = e.Value.Device,
                at = Local(e.Value.At),
                text = e.Value.Text,
                images = e.Value.Images.Count > 0 ? e.Value.Images.Count : (int?)null,
                black_box_events = e.Value.BlackBox is not null ? e.Value.BlackBoxEvents : (int?)null,
            }),
            black_box = box is null ? null : Summary(box, latest!.Id),
            next = "bug_blackbox for every event, bug_images for the pictures, bug_claim before you work on it.",
        };
    }

    private static object Summary(BlackBoxSnapshot box, string entry)
    {
        string At(BlackBoxEvent e) => box.SecondsBefore(e).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        return new
        {
            entry,
            frozen_at = Local(box.FrozenAt),
            events = box.Events.Count,
            steps = box.Events.Where(e => e.Kind is BlackBoxKind.Input or BlackBoxKind.Navigation or BlackBoxKind.DisabledClick)
                .Select(e => $"{At(e)}: {e.Text}" + (e.Kind == BlackBoxKind.DisabledClick ? " (it was disabled)" : "") + (e.Source is { } s ? $" [{s}]" : "")),
            errors = box.Events.Where(e => e.Kind == BlackBoxKind.Error).Select(e => new
            {
                at = At(e),
                text = e.Text,
                source = e.Source,
                stack = e.Detail is { } d ? BlackBox.Clip(d, MaxStack) : null,
            }),
            freezes = box.Events.Where(e => e.Kind == BlackBoxKind.Performance).Select(e => $"{At(e)}: {e.Text}"),
            helm = box.Context,
        };
    }

    private async Task<object?> BlackBoxOf(JsonElement args, CancellationToken ct)
    {
        var (id, bug) = Bug(args);
        var entry = Entry(args, id) ?? store.LatestBlackBox(id) ?? throw new McpToolException($"#{bug.Code} has no black box.");
        var box = await store.ReadBlackBoxAsync(entry.Value, ct).ConfigureAwait(false) ?? throw new McpToolException("That entry has no black box.");
        var kinds = McpArgs.Strings(args, "kinds");
        foreach (var k in kinds)
            if (!Kinds.Contains(k)) throw new ArgumentException($"kinds are among {string.Join(", ", Kinds)}.");
        var events = box.Events.Where(e => kinds.Count == 0 || kinds.Contains(KindName(e.Kind)));
        return new
        {
            bug = bug.Code,
            entry = entry.Id,
            frozen_at = Local(box.FrozenAt),
            events = events.Select(e => new
            {
                at = Math.Round(box.SecondsBefore(e), 1),
                kind = KindName(e.Kind),
                text = e.Text,
                source = e.Source,
                detail = e.Detail,
            }),
            helm = box.Context,
        };
    }

    private async Task<object?> Images(JsonElement args, CancellationToken ct)
    {
        var (id, bug) = Bug(args);
        var only = Entry(args, id);
        var images = store.Entries(id).Where(e => only is null || e.Id == only.Id)
            .SelectMany(e => e.Value.Images.Select((image, i) => (Entry: e, Image: image, Index: i))).ToList();
        if (images.Count == 0) return $"#{bug.Code} has no pictures.";
        var folder = Path.Combine(imageFolder?.Invoke() ?? Path.Combine(Path.GetTempPath(), "helm-bugs"), bug.Code);
        Directory.CreateDirectory(folder);
        var content = new List<McpImage>();
        var files = new List<object>();
        foreach (var (entry, image, index) in images)
        {
            var bytes = await store.ReadImageAsync(image, ct).ConfigureAwait(false);
            var path = Path.Combine(folder, $"{entry.Id}-{index + 1}{Extension(image.MimeType)}");
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            content.Add(new McpImage(image.MimeType, bytes));
            files.Add(new { entry = entry.Id, name = image.Name, file = path });
        }
        return new McpToolResult(new { bug = bug.Code, images = files }, Images: content);
    }

    // ---- helpers ----

    private (string Id, BugReport Bug) Bug(JsonElement args)
    {
        var key = McpArgs.RequiredString(args, "bug");
        var found = store.Find(key) ?? throw new KeyNotFoundException($"No bug {key}. bugs_list lists them.");
        return (found.Id, found.Value);
    }

    private string BugId(JsonElement args) => Bug(args).Id;

    private SyncedItem<BugEntry>? Entry(JsonElement args, string bugId)
    {
        if (McpArgs.String(args, "entry") is not { } entry) return null;
        return store.Entries(bugId).FirstOrDefault(e => e.Id == entry) ?? throw new KeyNotFoundException($"No entry {entry} on this bug.");
    }

    /// <summary>"Claude Code · helm": the calling client's name, and the project when given.</summary>
    internal static string Agent(JsonElement args)
    {
        var client = McpServer.CallingClient;
        var name = client is null ? "AI agent"
            : client.Contains("claude", StringComparison.OrdinalIgnoreCase) && client.Contains("code", StringComparison.OrdinalIgnoreCase) ? "Claude Code"
            : client;
        return McpArgs.String(args, "project") is { Length: > 0 } project ? $"{name} · {project.Trim()}" : name;
    }

    private object Changed(BugChange change) => new
    {
        bug = change.After.Code,
        status = StatusName(change.After.Status),
        summary = $"#{change.After.Code} {change.After.Title}: {Shown(change.After.Status)}",
        change = JsonSerializer.SerializeToElement(new UndoData(change.BugId, change.Before, change.EntryId), UndoJson),
    };

    private Task UndoChange(JsonElement result, CancellationToken ct)
    {
        var data = result.GetProperty("change").Deserialize<UndoData>(UndoJson) ?? throw new McpToolException("Nothing to take back.");
        McpTool.Undone(store.Undo(new BugChange(data.BugId, data.Before, data.Before, data.EntryId)));
        return Task.CompletedTask;
    }

    private sealed record UndoData(string BugId, BugReport Before, string? EntryId);

    private static readonly JsonSerializerOptions UndoJson = new(Helm.Core.Settings.HelmJson.Options) { WriteIndented = false };

    internal static string StatusName(BugStatus status) => status switch
    {
        BugStatus.Open => "open",
        BugStatus.Fixing => "fixing",
        BugStatus.ToVerify => "to_verify",
        _ => "closed",
    };

    private static string Shown(BugStatus status) => status switch
    {
        BugStatus.Open => "open",
        BugStatus.Fixing => "being fixed",
        BugStatus.ToVerify => "waiting for the user to check",
        _ => "closed",
    };

    private static string SeverityName(BugSeverity severity) => severity switch
    {
        BugSeverity.Blocking => "blocks the user",
        BugSeverity.Annoying => "annoying",
        _ => "minor",
    };

    private static string KindName(BlackBoxKind kind) => kind switch
    {
        BlackBoxKind.Input => "input",
        BlackBoxKind.Navigation => "navigation",
        BlackBoxKind.Log => "log",
        BlackBoxKind.Error => "error",
        BlackBoxKind.Performance => "performance",
        BlackBoxKind.Sync => "sync",
        _ => "disabled_click",
    };

    private static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Extension(string mime) => mime switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".img",
    };
}
