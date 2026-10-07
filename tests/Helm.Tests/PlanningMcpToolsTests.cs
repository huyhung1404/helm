using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Links;
using Helm.Core.Mcp;
using Helm.Modules.Missions;
using Helm.Modules.Notes;
using Helm.Modules.Tracker;
using Helm.Modules.Wallet;

namespace Helm.Tests;

/// <summary>What Claude can do by hand in Missions and Tracker: statuses, steps, checklists, lists, subtasks, repeats; and every tool's risk.</summary>
public sealed class PlanningMcpToolsTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero));
    private readonly MissionsStore _missions;
    private readonly TrackerStore _tracker;

    public PlanningMcpToolsTests()
    {
        _missions = new MissionsStore(new MemorySynced<Mission>(), new MemorySynced<MissionStep>(), new MemorySyncedLog<MissionEvent>(), _time);
        _tracker = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
    }

    private McpServer Server() => new(new IMcpToolProvider[] { new MissionsMcpTools(_missions), new TrackerMcpTools(_tracker) }.SelectMany(p => p.Tools), "1.0", "");

    private static JsonObject Plan() => new()
    {
        ["title"] = "Run 5 km",
        ["phases"] = new JsonArray(new JsonObject
        {
            ["title"] = "Base",
            ["steps"] = new JsonArray(
                new JsonObject { ["title"] = "Run 1 km", ["checklist"] = new JsonArray("Shoes", "Warm up") },
                new JsonObject { ["title"] = "Run 3 km" },
                new JsonObject { ["title"] = "Run 5 km" }),
        }),
    };

    [Fact]
    public void Every_tool_says_whether_it_only_reads_and_none_deletes_for_good()
    {
        var links = new LinkHub(new MemorySynced<HelmLink>(), () => [], _time);
        var notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        var wallet = new WalletStore(new MemorySynced<WalletTransaction>(), new MemorySynced<WalletCategory>(), new MemorySynced<WalletBudget>(), _time);
        var tools = new IMcpToolProvider[]
        {
            new MissionsMcpTools(_missions), new TrackerMcpTools(_tracker), new NotesMcpTools(notes, links), new WalletMcpTools(new DebtBook(new MemorySynced<WalletDebt>(), wallet, _time)),
        }.SelectMany(p => p.Tools).ToList();

        foreach (var tool in tools)
        {
            Assert.Equal(tool.ReadOnly ? McpRisk.Read : McpRisk.Change, tool.Risk);
            Assert.DoesNotContain("delete", tool.Name);
        }
        var readers = tools.Where(t => t.ReadOnly).Select(t => t.Name).Order().ToList();
        Assert.Equal(["mission_get", "missions_list", "notes_read", "notes_search", "tracker_lists", "tracker_tasks", "wallet_debts"], readers);
    }

    [Fact]
    public async Task Claude_starts_pauses_resumes_and_abandons_a_mission_without_deleting_it()
    {
        var server = Server();
        Assert.False((await Call(server, "mission_create", Plan())).IsError);
        var id = Assert.Single(_missions.Missions()).Id;

        var pause = await Call(server, "mission_set_status", new { mission = id, status = "pause" });
        Assert.True(pause.IsError);
        Assert.Contains("only an active mission can be paused", pause.Text);

        Assert.False((await Call(server, "mission_set_status", new { mission = "run 5 km", status = "start" })).IsError);
        Assert.Equal(MissionStatus.Active, _missions.GetMission(id)!.Status);
        Assert.False((await Call(server, "mission_set_status", new { mission = id, status = "pause" })).IsError);
        Assert.Equal(MissionStatus.Paused, _missions.GetMission(id)!.Status);
        Assert.False((await Call(server, "mission_set_status", new { mission = id, status = "abandon" })).IsError);
        Assert.Equal(MissionStatus.Abandoned, _missions.GetMission(id)!.Status);
        Assert.False((await Call(server, "mission_set_status", new { mission = id, status = "resume" })).IsError);
        Assert.Equal(MissionStatus.Active, _missions.GetMission(id)!.Status);
        Assert.Equal(3, _missions.Steps(id).Count);
    }

    [Fact]
    public async Task Claude_ticks_the_checklist_skips_and_reopens_steps_and_edits_them()
    {
        var server = Server();
        var plan = Plan();
        plan["start"] = true;
        await Call(server, "mission_create", plan);
        var id = Assert.Single(_missions.Missions()).Id;

        var tick = await Call(server, "mission_check_item", new { mission = id, item = 2 });
        Assert.False(tick.IsError, tick.Text);
        Assert.Contains("[x] Warm up", tick.Text);
        await Call(server, "mission_check_item", new { mission = id, item = 2 }); // already ticked: stays ticked
        Assert.True(_missions.Steps(id)[0].Value.Checklist[1].IsDone);
        await Call(server, "mission_check_item", new { mission = id, item = 2, done = false });
        Assert.False(_missions.Steps(id)[0].Value.Checklist[1].IsDone);
        Assert.True((await Call(server, "mission_check_item", new { mission = id, item = 3 })).IsError);

        var skipped = await Call(server, "mission_skip_step", new { mission = id, note = "did it last week" });
        Assert.Contains("\"skipped\":\"Run 1 km\"", skipped.Text);
        Assert.True(_missions.Steps(id)[0].Value.Skipped);

        var reopened = await Call(server, "mission_reopen_step", new { mission = id });
        Assert.False(reopened.IsError, reopened.Text);
        Assert.Contains("\"current_step\":\"Run 1 km\"", reopened.Text);

        var read = JsonNode.Parse((await Call(server, "mission_get", new { mission = id })).Text)!;
        var steps = read["phases"]![0]!["steps"]!.AsArray();
        var second = steps[1]!["id"]!.GetValue<string>();
        var edited = await Call(server, "mission_edit_step", new { step = second, title = "Run 2 km", estimate_days = 2, done_when = "2 km without stopping" });
        Assert.False(edited.IsError, edited.Text);
        Assert.Equal("Run 2 km", _missions.GetStep(second)!.Title);
        Assert.Equal(2, _missions.GetStep(second)!.EstimateDays);

        await Call(server, "mission_complete_step", new { mission = id });
        var first = steps[0]!["id"]!.GetValue<string>();
        var doneEdit = await Call(server, "mission_edit_step", new { step = first, title = "Changed" });
        Assert.True(doneEdit.IsError);
        Assert.Contains("only its note", doneEdit.Text);
        Assert.False((await Call(server, "mission_edit_step", new { step = first, note = "felt easy" })).IsError);
        Assert.Equal("felt easy", _missions.GetStep(first)!.Note);
    }

    [Fact]
    public async Task Claude_changes_a_missions_title_goal_and_deadline()
    {
        var server = Server();
        await Call(server, "mission_create", Plan());
        var id = Assert.Single(_missions.Missions()).Id;

        var updated = await Call(server, "mission_update", new { mission = id, goal = "5 km under 30 min", deadline = "2026-12-31" });
        Assert.False(updated.IsError, updated.Text);
        Assert.Equal("5 km under 30 min", _missions.GetMission(id)!.Goal);
        Assert.Equal(new DateOnly(2026, 12, 31), _missions.GetMission(id)!.Deadline);
        Assert.True((await Call(server, "mission_update", new { mission = id, deadline = "soon" })).IsError);
        Assert.True((await Call(server, "mission_update", new { mission = id, title = " " })).IsError);
        await Call(server, "mission_update", new { mission = id, clear_deadline = true });
        Assert.Null(_missions.GetMission(id)!.Deadline);
    }

    [Fact]
    public async Task Claude_adds_lists_subtasks_and_repeating_tasks_and_starts_a_task()
    {
        var server = Server();
        var list = await Call(server, "tracker_add_list", new { name = "Home" });
        Assert.False(list.IsError, list.Text);
        Assert.True((await Call(server, "tracker_add_list", new { name = "home" })).IsError);

        var task = JsonNode.Parse((await Call(server, "tracker_add_task", new { title = "Paint", list = "Home" })).Text)!;
        var taskId = task["id"]!.GetValue<string>();
        var sub = await Call(server, "tracker_add_task", new { title = "Buy paint", parent = taskId });
        Assert.False(sub.IsError, sub.Text);
        var subId = JsonNode.Parse(sub.Text)!["id"]!.GetValue<string>();
        Assert.Equal(taskId, _tracker.GetItem(subId)!.ParentId);
        Assert.True((await Call(server, "tracker_add_task", new { title = "Nested", parent = subId })).IsError);

        Assert.False((await Call(server, "tracker_start_task", new { id = taskId })).IsError);
        Assert.True(_tracker.GetItem(taskId)!.StartedExplicitly);
        Assert.False((await Call(server, "tracker_complete_task", new { id = subId })).IsError);
        Assert.True(_tracker.GetItem(subId)!.IsCompleted);
        Assert.False(_tracker.GetItem(taskId)!.IsCompleted);

        var daily = await Call(server, "tracker_add_task", new { title = "Stretch", list = "Home", repeat_daily = true, repeat_until = "2026-10-31" });
        Assert.False(daily.IsError, daily.Text);
        Assert.Contains("every day until 2026-10-31", daily.Text);
        var dailyId = JsonNode.Parse(daily.Text)!["id"]!.GetValue<string>();
        Assert.True((await Call(server, "tracker_add_task", new { title = "x", repeat_until = "2026-10-31" })).IsError);

        await Call(server, "tracker_update_task", new { id = dailyId, stop_repeating = true });
        Assert.False(_tracker.GetItem(dailyId)!.RepeatDaily);
    }

    private static async Task<(bool IsError, string Text)> Call(McpServer server, string tool, object args)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = (args as JsonNode)?.DeepClone() ?? JsonNode.Parse(JsonSerializer.Serialize(args)) },
        };
        var reply = await server.HandleAsync(request.ToJsonString(), default);
        var result = reply!["result"]!;
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }
}
