using System.Globalization;
using System.Text.Json;
using Helm.Core.Mcp;
using Helm.Core.Sync;

namespace Helm.Ai.Auto;

/// <summary>
/// The automatic jobs for Ask Helm (and AI agents over MCP): read the briefs the PC wrote ("bản tin sáng nay nói gì?"),
/// take back what a job changed, and see or set which jobs run and when.
/// </summary>
/// <param name="tools">Every tool provider, for the Undo of what a job changed (read when undoing).</param>
public sealed class AiBriefTools(AiBriefBook briefs, ISyncedSettings<AiFeatureSettings> features, Func<IEnumerable<IMcpToolProvider>> tools)
    : IMcpToolProvider
{
    private const int DefaultLimit = 3;

    public string? ModuleId => null;

    public IEnumerable<McpTool> Tools =>
    [
        new("ai_briefs",
            "The short briefs Helm's PC wrote by itself, newest first: the morning brief, the evening review, habit coaching, the spending " +
            "digest, important days, note titles, Scratch suggestions. Reading them marks them read.",
            McpArgs.Schema(
                ("kind", McpArgs.OneOf("Only one job's briefs.", [.. AutoJobCatalog.All.Select(j => j.Feature)]), false),
                ("limit", McpArgs.Number($"How many (default {DefaultLimit})."), false)),
            (args, _) => Task.FromResult<object?>(Read(args))) { ReadOnly = true },
        new("ai_brief_undo", "Take back what an automatic job changed, as its brief lists it (the titles and tags it gave notes).",
            McpArgs.Schema(("id", McpArgs.Text("The brief's id (from ai_briefs)."), true)),
            (args, ct) => Undo(McpArgs.RequiredString(args, "id"), ct)),
        new("ai_auto_jobs", "The jobs Helm's PC runs by itself on its AI agents: each one's id, name, whether it is on, its time and when it runs.",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(Jobs())) { ReadOnly = true },
        new("ai_auto_job_set", "Turn an automatic job on or off, or change its time of day.",
            McpArgs.Schema(
                ("job", McpArgs.OneOf("The job's id.", [.. AutoJobCatalog.All.Select(j => j.Feature)]), true),
                ("on", McpArgs.Flag("On (true) or off (false)."), false),
                ("at", McpArgs.Text("Its time of day, 24 h: 07:00 or 21:30."), false)),
            (args, _) => Task.FromResult<object?>(Set(args)))
        {
            Undo = (result, _) =>
            {
                var job = result.GetProperty("job").GetString()!;
                var before = result.GetProperty("before");
                Apply(job, before.GetProperty("on").GetBoolean(), before.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null);
                return Task.CompletedTask;
            },
        },
    ];

    private object Read(JsonElement args)
    {
        var kind = McpArgs.String(args, "kind");
        var limit = Math.Clamp(McpArgs.Int(args, "limit") ?? DefaultLimit, 1, 20);
        var latest = briefs.Latest(limit, kind);
        briefs.MarkRead(latest.Select(b => b.Id));
        return latest.Select(b => new
        {
            id = b.Id,
            kind = b.Value.Kind,
            job = AutoJobCatalog.Find(b.Value.Kind)?.Name,
            title = b.Value.Title,
            text = b.Value.Text,
            written = TimeZoneInfo.ConvertTime(b.Value.CreatedAt, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            wasRead = b.Value.ReadAt is not null,
            canUndo = b.Value.Actions.Count > 0 && b.Value.UndoneAt is null ? true : (bool?)null,
            undone = b.Value.UndoneAt is not null ? true : (bool?)null,
        }).ToList();
    }

    private async Task<object?> Undo(string id, CancellationToken ct)
    {
        var brief = briefs.Get(id) ?? throw new McpToolException($"There is no brief {id}.");
        if (brief.UndoneAt is not null) return new { id, undone = 0, note = "already undone" };
        if (brief.Actions.Count == 0) throw new McpToolException("That brief changed nothing.");
        var all = tools().SelectMany(p => p.Tools).ToList();
        var undone = 0;
        foreach (var action in brief.Actions)
        {
            if (all.FirstOrDefault(t => t.Name == action.Tool)?.Undo is not { } undo) continue;
            await undo(JsonDocument.Parse(action.ResultJson).RootElement.Clone(), ct).ConfigureAwait(false);
            undone++;
        }
        briefs.MarkUndone(id);
        return new { id, undone };
    }

    private object Jobs() => AutoJobCatalog.All.Select(j =>
    {
        var choice = features.Current.For(j.Feature);
        return new
        {
            job = j.Feature,
            name = j.Name,
            on = choice.Enabled,
            at = j.Feature == AiFeatureIds.NoteTitles ? null : AutoJobCatalog.TimeOf(choice, j).ToString("HH:mm", CultureInfo.InvariantCulture),
            when = j.When,
            mayUseApi = AutoJobAsker.MayUseApi(choice) ? true : (bool?)null,
        };
    }).ToList();

    private object Set(JsonElement args)
    {
        var job = McpArgs.RequiredString(args, "job");
        if (AutoJobCatalog.Find(job) is not { } info) throw new McpToolException($"There is no automatic job {job}; call ai_auto_jobs.");
        var at = McpArgs.String(args, "at");
        if (at is not null && AutoJobCatalog.ParseTime(at) is null) throw new McpToolException($"{at} is not a time like 07:00.");
        var before = features.Current.For(job);
        var on = McpArgs.Bool(args, "on") ?? before.Enabled;
        Apply(job, on, at ?? before.At);
        var now = features.Current.For(job);
        return new
        {
            job,
            name = info.Name,
            on = now.Enabled,
            at = AutoJobCatalog.TimeOf(now, info).ToString("HH:mm", CultureInfo.InvariantCulture),
            before = new { on = before.Enabled, at = before.At },
        };
    }

    private void Apply(string job, bool on, string? at) => features.Update(s =>
    {
        var choice = s.For(job).Copy();
        choice.Enabled = on;
        choice.At = AutoJobCatalog.ParseTime(at)?.ToString("HH:mm", CultureInfo.InvariantCulture);
        s.Features[job] = choice;
    });
}
