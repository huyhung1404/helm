using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Core.Sync;

namespace Helm.Modules.Missions;

/// <summary>
/// Missions for Claude over MCP: read the missions, create one (the same format and rules as the JSON import), and
/// complete the current step. Steps are done in order, so only the current step can be completed. Nothing is ever
/// deleted through here. Dates are the user's local ones.
/// </summary>
public sealed class MissionsMcpTools(MissionsStore store) : IMcpToolProvider
{
    public string? ModuleId => MissionsIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("missions_list", "The user's missions (goals reached through ordered steps): status, progress, the current step, pace and deadline.",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(List())) { ReadOnly = true },
        new("mission_get", "One mission with its phases and every step: what to do, how to check it is done, dates, notes; plus its pace.",
            McpArgs.Schema(("mission", McpArgs.Text("The mission's id or title."), true)),
            (args, _) => Task.FromResult<object?>(Get(McpArgs.RequiredString(args, "mission")))) { ReadOnly = true },
        new("mission_create",
            "Create a mission: a goal split into phases of concrete steps done in order. Write titles and texts in the user's language. " +
            "Each step should take 0.5-3 days, with a doneWhen the user can check; end each phase with a review step; give each phase a small " +
            "reward and the mission a bigger one. The mission is created as not started; the user starts it in Helm (or pass start: true).",
            CreateSchema(), (args, _) => Task.FromResult<object?>(Create(args))),
        new("mission_complete_step", "Complete the current step of a mission (steps are done in order), with an optional note on what was done.",
            McpArgs.Schema(
                ("mission", McpArgs.Text("The mission's id or title."), true),
                ("note", McpArgs.Text("What was done, a result or a link (optional)."), false)),
            (args, _) => Task.FromResult<object?>(CompleteStep(args))),
        new("mission_replan",
            "Plan the rest of a mission again, e.g. when the user is behind or something changed: every step not done yet (the current one " +
            "too) is replaced by these phases and steps; done steps stay. Read mission_get first to see what is done and how long steps took. " +
            "Keep a phase's exact title to go on with it. Ask the user before replacing their plan.",
            ReplanSchema(), (args, _) => Task.FromResult<object?>(Replan(args))),
    ];

    private object Replan(JsonElement args)
    {
        var mission = Require(McpArgs.RequiredString(args, "mission"));
        if (mission.Value.Status is MissionStatus.Completed or MissionStatus.Abandoned)
            throw new McpToolException($"“{mission.Value.Title}” is {mission.Value.Status.ToString().ToLowerInvariant()}; there is nothing left to plan.");
        var result = MissionImport.Read(args, requireTitle: false);
        if (!result.Ok) throw new McpToolException(string.Join(" ", result.Errors));
        if (!store.Replan(mission.Id, result.Draft!)) throw new McpToolException($"The plan is too big: at most {MissionLimits.Steps} steps and {MissionLimits.Phases} phases in all.");
        var updated = (JsonObject)Get(mission.Id);
        if (result.Warnings.Count > 0) updated["warnings"] = new JsonArray(result.Warnings.Select(w => (JsonNode?)w).ToArray());
        return updated;
    }

    private object List()
    {
        var now = store.Now;
        return store.Missions().Select(m => Describe(m.Id, m.Value, store.Steps(m.Id).Select(s => s.Value).ToList(), now, withSteps: false)).ToList();
    }

    private object Get(string idOrTitle)
    {
        var mission = Require(idOrTitle);
        var steps = store.Steps(mission.Id);
        var summary = (JsonObject)Describe(mission.Id, mission.Value, steps.Select(s => s.Value).ToList(), store.Now, withSteps: true);
        var zone = TimeZoneInfo.Local;
        summary["phases"] = new JsonArray(mission.Value.Phases.Select(p => (JsonNode?)new JsonObject
        {
            ["title"] = p.Title,
            ["reward"] = p.Reward.Length > 0 ? p.Reward : null,
            ["steps"] = new JsonArray(steps.Where(s => s.Value.PhaseKey == p.Key).Select(s => (JsonNode?)Step(s, zone)).ToArray()),
        }).ToArray());
        return summary;
    }

    private object Create(JsonElement args)
    {
        var result = MissionImport.Read(args);
        if (!result.Ok) throw new McpToolException(string.Join(" ", result.Errors));
        var id = store.Create(result.Draft!, MissionSource.Claude);
        if (McpArgs.Bool(args, "start") == true) store.Start(id);
        var created = (JsonObject)Get(id);
        if (result.Warnings.Count > 0) created["warnings"] = new JsonArray(result.Warnings.Select(w => (JsonNode?)w).ToArray());
        return created;
    }

    private object CompleteStep(JsonElement args)
    {
        var mission = Require(McpArgs.RequiredString(args, "mission"));
        switch (mission.Value.Status)
        {
            case MissionStatus.Planned: throw new McpToolException($"“{mission.Value.Title}” has not been started; the user starts it in Helm.");
            case MissionStatus.Paused: throw new McpToolException($"“{mission.Value.Title}” is paused; the user resumes it in Helm.");
            case MissionStatus.Abandoned: throw new McpToolException($"“{mission.Value.Title}” was abandoned.");
            case MissionStatus.Completed: throw new McpToolException($"“{mission.Value.Title}” is already complete.");
        }
        var outcome = store.Complete(mission.Id, McpArgs.String(args, "note")) ?? throw new McpToolException("There is no step left to complete.");
        var node = new JsonObject
        {
            ["completed"] = outcome.StepTitle,
            ["progress"] = $"{outcome.Done}/{outcome.Total}",
            ["phase_completed"] = outcome.PhaseCompleted?.Title,
            ["phase_reward"] = outcome.PhaseCompleted is { Reward.Length: > 0 } p ? p.Reward : null,
            ["mission_completed"] = outcome.MissionCompleted,
            ["mission_reward"] = outcome.MissionCompleted && mission.Value.Reward.Length > 0 ? mission.Value.Reward : null,
            ["next_step"] = outcome.NextStepTitle,
        };
        foreach (var key in node.Where(p => p.Value is null).Select(p => p.Key).ToList()) node.Remove(key);
        return node;
    }

    private static JsonNode Describe(string id, Mission m, IReadOnlyList<MissionStep> steps, DateTimeOffset now, bool withSteps)
    {
        var zone = TimeZoneInfo.Local;
        var pace = MissionPace.Compute(m, steps, [], now, zone);
        var current = steps.FirstOrDefault(s => !s.IsDone);
        var node = new JsonObject
        {
            ["id"] = id,
            ["title"] = m.Title,
            ["goal"] = m.Goal.Length > 0 ? m.Goal : null,
            ["status"] = m.Status.ToString().ToLowerInvariant(),
            ["progress"] = $"{pace.Done}/{pace.Total} steps",
            ["phase"] = m.Phases.Count > 0 ? $"{pace.PhaseIndex + 1}/{m.Phases.Count}: {m.Phases[pace.PhaseIndex].Title}" : null,
            ["current_step"] = current?.Title,
            ["pace"] = m.Status == MissionStatus.Active ? MissionsFormat.Pace(pace).ToLowerInvariant() : null,
            ["projected_finish"] = Day(pace.ProjectedFinish),
            ["deadline"] = Day(m.Deadline),
            ["started"] = Day(m.StartedAt is { } s ? MissionPace.Day(s, zone) : null),
            ["completed"] = Day(m.CompletedAt is { } c ? MissionPace.Day(c, zone) : null),
            ["reward"] = withSteps && m.Reward.Length > 0 ? m.Reward : null,
            ["note"] = withSteps && m.Note.Length > 0 ? m.Note : null,
        };
        foreach (var key in node.Where(p => p.Value is null).Select(p => p.Key).ToList()) node.Remove(key);
        return node;
    }

    private static JsonObject Step(SyncedItem<MissionStep> item, TimeZoneInfo zone)
    {
        var s = item.Value;
        var node = new JsonObject
        {
            ["title"] = s.Title,
            ["description"] = s.Description.Length > 0 ? s.Description : null,
            ["done_when"] = s.DoneWhen.Length > 0 ? s.DoneWhen : null,
            ["estimate_days"] = s.EstimateDays,
            ["checklist"] = s.Checklist.Count > 0 ? new JsonArray(s.Checklist.Select(c => (JsonNode?)((c.IsDone ? "[x] " : "[ ] ") + c.Text)).ToArray()) : null,
            ["resources"] = s.Resources.Count > 0 ? new JsonArray(s.Resources.Select(r => (JsonNode?)r).ToArray()) : null,
            ["state"] = s.Skipped ? "skipped" : s.IsDone ? "done" : "open",
            ["started"] = s.StartedExplicitly && s.StartedAt is { } st ? Day(MissionPace.Day(st, zone)) : null,
            ["done"] = s.CompletedAt is { } c ? Day(MissionPace.Day(c, zone)) : null,
            ["note"] = s.Note.Length > 0 ? s.Note : null,
        };
        foreach (var key in node.Where(p => p.Value is null).Select(p => p.Key).ToList()) node.Remove(key);
        return node;
    }

    private SyncedItem<Mission> Require(string idOrTitle) =>
        store.Find(idOrTitle) ?? throw new McpToolException(
            store.Missions() is { Count: > 0 } all
                ? $"There is no single mission {idOrTitle}. Missions: {string.Join(", ", all.Select(m => $"{m.Value.Title} ({m.Id})"))}."
                : "There are no missions yet.");

    private static string? Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The import format as a JSON Schema.</summary>
    private static JsonObject CreateSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = McpArgs.Text("Short name of the mission."),
            ["goal"] = McpArgs.Text("The measurable end result."),
            ["deadline"] = McpArgs.Text("Optional: 2027-03-01."),
            ["reward"] = McpArgs.Text("A bigger reward for finishing the whole mission."),
            ["note"] = McpArgs.Text("Optional: anything the user should know, e.g. the deadline is tight."),
            ["phases"] = PhasesSchema("The phases in order."),
            ["start"] = McpArgs.Flag("Start the mission now (default: the user starts it in Helm)."),
        },
        ["required"] = new JsonArray("title", "phases"),
    };

    private static JsonObject ReplanSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["mission"] = McpArgs.Text("The mission's id or title."),
            ["phases"] = PhasesSchema("Only the steps still to do, in order, starting with the next one. Keep a phase's exact title to go on with it."),
            ["deadline"] = McpArgs.Text("Optional: a new deadline, 2027-03-01, when the old one cannot be met."),
            ["note"] = McpArgs.Text("Optional: why the plan changed, shown on the mission."),
        },
        ["required"] = new JsonArray("mission", "phases"),
    };

    private static JsonObject PhasesSchema(string description)
    {
        JsonObject Str(string text) => McpArgs.Text(text);
        JsonObject Strings(string description) => McpArgs.List(description);
        var step = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["title"] = Str("One concrete piece of work."),
                ["description"] = Str("How to do it."),
                ["doneWhen"] = Str("How the user can check it is done: a number, a test, something produced."),
                ["estimateDays"] = new JsonObject { ["type"] = "number", ["description"] = "Realistic days at the user's pace (0.25-60)." },
                ["checklist"] = Strings("Optional ticks that split the step (up to 7)."),
                ["resources"] = Strings("Optional real links or book titles."),
            },
            ["required"] = new JsonArray("title"),
        };
        var phase = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["title"] = Str("Phase name."),
                ["reward"] = Str("A small reward for finishing the phase."),
                ["steps"] = new JsonObject { ["type"] = "array", ["items"] = step, ["description"] = "The steps in order." },
            },
            ["required"] = new JsonArray("title", "steps"),
        };
        return new JsonObject { ["type"] = "array", ["items"] = phase, ["description"] = description };
    }
}
