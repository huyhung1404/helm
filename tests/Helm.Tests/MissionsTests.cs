using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Capture;
using Helm.Core.Links;
using Helm.Core.Mcp;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Missions;
using Helm.Modules.Notes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public sealed class MissionsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(T0);
    private readonly MemorySynced<Mission> _missions = new();
    private readonly MemorySynced<MissionStep> _steps = new();
    private readonly MemorySyncedLog<MissionEvent> _history = new();
    private readonly MissionsStore _store;
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public MissionsTests()
    {
        _store = new MissionsStore(_missions, _steps, _history, _time);
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    private const string Hsk = """
        Here is your roadmap!

        ```json
        {
          "helmMission": 1,
          "title": "Pass HSK3",
          "goal": "Score 210+",
          "deadline": "2027-03-01",
          "reward": "New books",
          "phases": [
            {
              "title": "Foundations",
              "reward": "Hotpot",
              "steps": [
                { "title": "HSK1 words", "doneWhen": "90% on a self-test", "estimateDays": 3, "checklist": ["1-50", "51-100"], "resources": ["https://example.com/hsk1"] },
                { "title": "HSK1 review", "estimateDays": 1 },
              ]
            },
            {
              "title": "HSK2",
              "steps": [
                { "title": "HSK2 words", "estimateDays": 4 },
                { "title": "Mock test", "estimateDays": 1 }
              ]
            }
          ]
        }
        ```
        Good luck!
        """;

    private string CreateHsk(bool start = true)
    {
        var result = MissionImport.Parse(Hsk);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        var id = _store.Create(result.Draft!, MissionSource.Import);
        if (start) Assert.True(_store.Start(id));
        return id;
    }

    // ---- Import --------------------------------------------------------------------------------------------------

    [Fact]
    public void Import_finds_the_json_in_an_answer_with_text_and_a_fence_and_trailing_commas()
    {
        var result = MissionImport.Parse(Hsk);

        Assert.True(result.Ok);
        var draft = result.Draft!;
        Assert.Equal("Pass HSK3", draft.Title);
        Assert.Equal(new DateOnly(2027, 3, 1), draft.Deadline);
        Assert.Equal(["Foundations", "HSK2"], draft.Phases.Select(p => p.Title));
        Assert.Equal(4, draft.StepCount);
        Assert.Equal(9, draft.EstimateDays);
        var first = draft.Phases[0].Steps[0];
        Assert.Equal("90% on a self-test", first.DoneWhen);
        Assert.Equal(["1-50", "51-100"], first.Checklist!);
        Assert.Equal("https://example.com/hsk1", Assert.Single(first.Resources!).Url);
    }

    [Fact]
    public void Import_forgives_typographic_quotes_steps_without_phases_and_numbers_as_text()
    {
        var text = "{“title”: “Run 10 km”, “steps”: [{“title”: “Run 3 km”, “days”: “2 days”}, “Run 5 km”]}";

        var result = MissionImport.Parse(text);

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        var phase = Assert.Single(result.Draft!.Phases);
        Assert.Equal("Run 10 km", phase.Title);
        Assert.Equal(["Run 3 km", "Run 5 km"], phase.Steps.Select(s => s.Title));
        Assert.Equal(2, phase.Steps[0].EstimateDays);
        Assert.Equal(1, phase.Steps[1].EstimateDays);
    }

    [Fact]
    public void Typographic_quotes_inside_texts_are_kept()
    {
        var result = MissionImport.Parse("{\"title\": \"Read “Dune”\", \"steps\": [\"Part 1\"]}");

        Assert.True(result.Ok);
        Assert.Equal("Read “Dune”", result.Draft!.Title);
    }

    [Theory]
    [InlineData("", "Paste the JSON")]
    [InlineData("Sure! I can help with that.", "There is no JSON here")]
    [InlineData("{\"title\": \"X\", \"phases\": [{\"title\": \"A\", \"steps\": [", "cut off")]
    [InlineData("{\"title\": \"X\", \"phases\": [ ]}", "no steps")]
    [InlineData("{\"phases\": [{\"steps\": [\"a\"]}]}", "no \"title\"")]
    [InlineData("{\"helmMission\": 2, \"title\": \"X\", \"steps\": [\"a\"]}", "Update Helm")]
    [InlineData("{\"title\": \"X\" \"steps\": []}", "mistake on line 1")]
    public void Import_explains_what_is_wrong(string text, string expected)
    {
        var result = MissionImport.Parse(text);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Import_names_the_step_without_a_title_and_warns_about_odd_values()
    {
        var bad = MissionImport.Parse("{\"title\": \"X\", \"phases\": [{\"title\": \"A\", \"steps\": [{\"title\": \"ok\"}, {\"description\": \"no title\"}]}]}");
        Assert.Equal(["Phase 1, step 2 has no title."], bad.Errors);

        var odd = MissionImport.Parse("{\"title\": \"X\", \"deadline\": \"soon\", \"steps\": [{\"title\": \"a\", \"estimateDays\": 400}]}");
        Assert.True(odd.Ok);
        Assert.Null(odd.Draft!.Deadline);
        Assert.Equal(MissionLimits.MaxEstimateDays, odd.Draft.Phases[0].Steps[0].EstimateDays);
        Assert.Equal(2, odd.Warnings.Count);
    }

    [Fact]
    public void The_prompt_carries_the_answers_and_its_example_imports()
    {
        var prompt = MissionPrompt.Build(new MissionPromptInput("Pass HSK3", "300 words", "1 hour a day", new DateOnly(2027, 3, 1)));

        Assert.Contains("My goal: Pass HSK3", prompt);
        Assert.Contains("Where I am now: 300 words", prompt);
        Assert.Contains("Deadline: 2027-03-01", prompt);
        Assert.DoesNotContain("Other notes", prompt);
        Assert.True(MissionImport.Parse(prompt).Ok, "the example inside the prompt imports");
        Assert.True(MissionImport.Parse(MissionPrompt.Template).Ok);
    }

    [Fact]
    public void A_mission_copied_as_json_imports_again()
    {
        var id = CreateHsk();
        _store.Complete(id, "done in 2 days");

        var json = MissionsFormat.Json(_store.GetMission(id)!, _store.Steps(id).Select(s => s.Value).ToList());
        var again = MissionImport.Parse(json);

        Assert.True(again.Ok);
        Assert.Equal(4, again.Draft!.StepCount);
        Assert.Contains("\"completedAt\"", json);
        Assert.Contains("done in 2 days", json);
    }

    // ---- Store: the order of steps -------------------------------------------------------------------------------

    [Fact]
    public void A_new_mission_is_planned_and_nothing_runs_until_it_starts()
    {
        var id = CreateHsk(start: false);

        Assert.Equal(MissionStatus.Planned, _store.GetMission(id)!.Status);
        Assert.Null(_store.Complete(id));
        Assert.False(_store.StartStep(id));
        Assert.True(_store.Start(id));
        Assert.False(_store.Start(id));
        Assert.Equal(T0, _store.GetMission(id)!.StartedAt);
        Assert.Equal("HSK1 words", _store.CurrentStep(id)!.Value.Title);
    }

    [Fact]
    public void Steps_are_done_in_order_and_their_dates_are_logged()
    {
        var id = CreateHsk();
        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(_store.StartStep(id));
        Assert.False(_store.StartStep(id));
        _time.Advance(TimeSpan.FromDays(2));

        var outcome = _store.Complete(id, "  learned them  ")!;

        Assert.Equal((1, 4, false, "HSK1 review"), (outcome.Done, outcome.Total, outcome.MissionCompleted, outcome.NextStepTitle));
        Assert.Null(outcome.PhaseCompleted);
        var first = _store.Steps(id)[0].Value;
        Assert.Equal(T0.AddHours(1), first.StartedAt);
        Assert.True(first.StartedExplicitly);
        Assert.Equal(T0.AddHours(1).AddDays(2), first.CompletedAt);
        Assert.Equal("learned them", first.Note);

        // Never started: it counts from the previous step's finish.
        _time.Advance(TimeSpan.FromHours(5));
        var second = _store.Complete(id)!;
        Assert.Equal(first.CompletedAt, _store.Steps(id)[1].Value.StartedAt);
        Assert.Equal("Foundations", second.PhaseCompleted?.Title);

        Assert.Equal(
            [MissionEventKind.MissionCreated, MissionEventKind.MissionStarted, MissionEventKind.StepStarted, MissionEventKind.StepCompleted,
             MissionEventKind.StepCompleted, MissionEventKind.PhaseCompleted],
            _store.History(id).Select(e => e.Kind));
    }

    [Fact]
    public void Only_the_current_step_can_be_ticked()
    {
        var id = CreateHsk();

        Assert.True(_store.ToggleChecklist(id, 1));
        Assert.False(_store.ToggleChecklist(id, 5));
        Assert.True(_store.Steps(id)[0].Value.Checklist[1].IsDone);
        Assert.True(_store.ToggleChecklist(id, 1));
        Assert.False(_store.Steps(id)[0].Value.Checklist[1].IsDone);
        // One tick logged (unticking is not progress).
        Assert.Single(_store.History(id), e => e.Kind == MissionEventKind.ChecklistTicked);
    }

    [Fact]
    public void The_last_step_finishes_the_mission_and_undo_brings_it_back()
    {
        var id = CreateHsk();
        _store.Complete(id);
        _store.Skip(id, "already known");
        _store.Complete(id);
        var last = _store.Complete(id)!;

        Assert.True(last.MissionCompleted);
        Assert.Equal("HSK2", last.PhaseCompleted?.Title);
        Assert.Equal(MissionStatus.Completed, _store.GetMission(id)!.Status);
        Assert.Null(_store.Complete(id));
        Assert.True(_store.Steps(id)[1].Value.Skipped);

        Assert.True(_store.ReopenLast(id));
        Assert.Equal(MissionStatus.Active, _store.GetMission(id)!.Status);
        Assert.Null(_store.GetMission(id)!.CompletedAt);
        Assert.Equal("Mock test", _store.CurrentStep(id)!.Value.Title);

        // Undo again reopens the step before it, whatever it was.
        Assert.True(_store.ReopenLast(id));
        Assert.Equal("HSK2 words", _store.CurrentStep(id)!.Value.Title);
    }

    [Fact]
    public void A_paused_mission_takes_no_steps_and_its_pause_is_left_out_of_the_pace()
    {
        var id = CreateHsk();
        _time.Advance(TimeSpan.FromDays(1));
        Assert.True(_store.Pause(id));
        Assert.Null(_store.Complete(id));
        _time.Advance(TimeSpan.FromDays(10));
        Assert.True(_store.Resume(id));

        var mission = _store.GetMission(id)!;
        Assert.Equal(MissionStatus.Active, mission.Status);
        Assert.Equal(TimeSpan.FromDays(10), mission.PausedTotal);
        var pace = MissionPace.Compute(mission, _store.Steps(id).Select(s => s.Value).ToList(), [], _store.Now, TimeZoneInfo.Utc);
        Assert.Equal(1, pace.ElapsedDays, 3);
        Assert.Equal(0, pace.DaysOff);
    }

    [Fact]
    public void An_abandoned_mission_can_be_taken_up_again()
    {
        var planned = CreateHsk(start: false);
        Assert.True(_store.Abandon(planned));
        Assert.True(_store.Resume(planned));
        Assert.Equal(MissionStatus.Planned, _store.GetMission(planned)!.Status);

        var running = CreateHsk();
        Assert.True(_store.Abandon(running));
        Assert.False(_store.Pause(running));
        Assert.True(_store.Resume(running));
        Assert.Equal(MissionStatus.Active, _store.GetMission(running)!.Status);
    }

    [Fact]
    public void Steps_not_done_can_be_edited_added_and_deleted_but_done_ones_keep_their_text()
    {
        var id = CreateHsk();
        _store.Complete(id);
        var steps = _store.Steps(id);

        Assert.False(_store.UpdateStep(steps[0].Id, "Changed", "", "", 1));
        Assert.True(_store.SetNote(steps[0].Id, "a note on a done step"));
        Assert.True(_store.UpdateStep(steps[2].Id, "HSK2 words (300)", "Anki", "all 300", 5));
        Assert.Equal(5, _store.GetStep(steps[2].Id)!.EstimateDays);
        Assert.False(_store.DeleteStep(steps[0].Id));
        Assert.True(_store.DeleteStep(steps[3].Id));

        var added = _store.AddStep(id, new StepDraft("Real exam", EstimateDays: 1));
        Assert.NotNull(added);
        Assert.Equal(["HSK1 words", "HSK1 review", "HSK2 words (300)", "Real exam"], _store.Steps(id).Select(s => s.Value.Title));
        Assert.Equal(_store.GetMission(id)!.Phases[1].Key, _store.GetStep(added)!.PhaseKey);

        // In the first phase: after its last step, still before the second phase.
        var inFirst = _store.AddStep(id, new StepDraft("Extra review"), _store.GetMission(id)!.Phases[0].Key);
        Assert.Equal(["HSK1 words", "HSK1 review", "Extra review", "HSK2 words (300)", "Real exam"], _store.Steps(id).Select(s => s.Value.Title));
        Assert.NotNull(inFirst);
    }

    [Fact]
    public void A_step_cannot_be_added_before_the_current_one()
    {
        var id = CreateHsk();
        _store.Complete(id);
        _store.Complete(id);
        _store.Complete(id); // now on "Mock test", in phase 2

        Assert.Null(_store.AddStep(id, new StepDraft("Too late"), _store.GetMission(id)!.Phases[0].Key));
    }

    [Fact]
    public void Two_devices_completing_the_same_step_do_not_complete_two()
    {
        var id = CreateHsk();
        var current = _store.CurrentStep(id)!;
        // The other device completed it first; its record arrives.
        _steps.SetRemote(current.Id, current.Value with { CompletedAt = T0.AddHours(2), StartedAt = T0 });

        var outcome = _store.Complete(id)!;

        Assert.Equal("HSK1 review", outcome.StepTitle);
        Assert.Equal(2, outcome.Done);
    }

    [Fact]
    public void Deleting_a_mission_removes_its_steps_and_keeps_its_history()
    {
        var id = CreateHsk();
        var other = CreateHsk();

        Assert.True(_store.Delete(id));

        Assert.Null(_store.GetMission(id));
        Assert.Empty(_store.Steps(id));
        Assert.Equal(4, _store.Steps(other).Count);
        Assert.NotEmpty(_store.History(id));
    }

    [Fact]
    public void Phase_and_mission_rewards_are_claimed_once_they_are_earned()
    {
        var id = CreateHsk();
        var foundations = _store.GetMission(id)!.Phases[0].Key;

        Assert.False(_store.ClaimReward(id, foundations));
        _store.Complete(id);
        _store.Complete(id);
        Assert.True(_store.ClaimReward(id, foundations));
        Assert.False(_store.ClaimReward(id, foundations));
        Assert.False(_store.ClaimReward(id));
        _store.Complete(id);
        _store.Complete(id);
        Assert.True(_store.ClaimReward(id));
        Assert.NotNull(_store.GetMission(id)!.RewardClaimedAt);
    }

    // ---- Pace ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Pace_says_how_far_behind_or_ahead_the_plan_is()
    {
        var id = CreateHsk(); // estimates 3, 1, 4, 1
        List<MissionStep> Steps() => _store.Steps(id).Select(s => s.Value).ToList();
        MissionPaceInfo Pace() => MissionPace.Compute(_store.GetMission(id)!, Steps(), _store.History(id), _store.Now, TimeZoneInfo.Utc);

        _time.Advance(TimeSpan.FromDays(5.5));
        Assert.Equal(2, Pace().DaysOff); // the first step was planned to end on day 3

        _store.Complete(id);
        _store.Complete(id);
        _time.Advance(TimeSpan.FromDays(0.5));
        var behind = Pace();
        Assert.Equal(0, behind.DaysOff); // 6 days in, step 3 ends on day 8
        Assert.Equal(9, behind.PlannedDays);
        // Done steps took 1.5× their plan: the 5 days left stretch to 7.5, minus the half day on step 3.
        Assert.Equal(DateOnly.FromDateTime(T0.AddDays(13).UtcDateTime), behind.ProjectedFinish);

        var fast = CreateHsk();
        _time.Advance(TimeSpan.FromDays(1));
        _store.Complete(fast);
        _store.Complete(fast);
        var ahead = MissionPace.Compute(_store.GetMission(fast)!, _store.Steps(fast).Select(s => s.Value).ToList(), [], _store.Now, TimeZoneInfo.Utc);
        Assert.Equal(-3, ahead.DaysOff);
        Assert.Equal("3 days ahead", MissionsFormat.Pace(ahead));
    }

    [Fact]
    public void Streaks_count_days_in_a_row_with_progress()
    {
        MissionEvent Done(int day) => new() { Kind = MissionEventKind.StepCompleted, At = T0.AddDays(day) };
        var history = new[] { Done(0), Done(1), Done(1), Done(2), Done(5), Done(6) };

        Assert.Equal((2, 3), MissionPace.Streaks(history, T0.AddDays(6), TimeZoneInfo.Utc));
        Assert.Equal((2, 3), MissionPace.Streaks(history, T0.AddDays(7), TimeZoneInfo.Utc)); // nothing yet today
        Assert.Equal((0, 3), MissionPace.Streaks(history, T0.AddDays(8), TimeZoneInfo.Utc));
    }

    [Fact]
    public void The_summary_lists_every_step_with_its_date_and_note()
    {
        var id = CreateHsk();
        _time.Advance(TimeSpan.FromDays(2));
        _store.Complete(id, "Anki deck done");
        _store.Skip(id);
        _store.Complete(id);
        _store.Complete(id);
        var mission = _store.GetMission(id)!;
        var steps = _store.Steps(id).Select(s => s.Value).ToList();

        var summary = MissionsFormat.Summary(mission, steps, MissionPace.Compute(mission, steps, _store.History(id), _store.Now, TimeZoneInfo.Utc), _store.Now, TimeZoneInfo.Utc);

        Assert.StartsWith("# Mission complete: Pass HSK3", summary);
        Assert.Contains("3 of 4 steps done, 1 skipped", summary);
        Assert.Contains("- ✓ HSK1 words", summary);
        Assert.Contains(": Anki deck done", summary);
        Assert.Contains("- ⤼ HSK1 review", summary);
    }

    // ---- Resources -----------------------------------------------------------------------------------------------

    /// <summary>A step whose resources carry their content: a table with the plan's own columns, a text, a link.</summary>
    private const string WithResources = """
        {
          "title": "Learn things",
          "steps": [
            {
              "title": "Words, part 1",
              "resources": [
                "Course book — https://example.com/book",
                {
                  "label": "Vocabulary",
                  "title": "Words 1-3",
                  "columns": ["Word", "Pinyin", "Meaning"],
                  "rows": [["担心", "dānxīn", "to worry"], ["附近", "fùjìn", "nearby"], ["根据", "gēnjù"]]
                },
                {
                  "kind": "Formulas",
                  "title": "Areas",
                  "text": "Learn these two.\nThen solve 10 problems.",
                  "items": [{ "shape": "Circle", "formula": "πr²" }, { "shape": "Square", "formula": "a²", "note": 4 }]
                },
                { "label": "Mock test", "title": "Test 1", "url": "https://example.com/test", "text": "Open Tests, then Level 3, then Test 1." },
                { "title": "Plain list", "items": ["one", "two"] },
                { "text": "An old-style book title" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Import_reads_resources_with_a_label_a_text_and_a_table_of_any_columns()
    {
        var result = MissionImport.Parse(WithResources);

        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Empty(result.Warnings);
        var r = result.Draft!.Phases[0].Steps[0].Resources!;
        Assert.Equal(6, r.Count);

        Assert.Equal(("Course book", "https://example.com/book"), (r[0].Title, r[0].Url));
        Assert.False(MissionResources.HasDetails(r[0]));

        Assert.Equal("Vocabulary", r[1].Label);
        Assert.Equal(["Word", "Pinyin", "Meaning"], r[1].Columns);
        Assert.Equal(["根据", "gēnjù", ""], r[1].Rows[2]); // a short row is filled up to the columns

        // Objects of any keys: the keys are the columns, in the order first seen; numbers are text.
        Assert.Equal("Formulas", r[2].Label);
        Assert.Equal(["shape", "formula", "note"], r[2].Columns);
        Assert.Equal(["Square", "a²", "4"], r[2].Rows[1]);
        Assert.Equal("Learn these two.\nThen solve 10 problems.", r[2].Text);

        Assert.Equal(("Mock test", "https://example.com/test"), (r[3].Label, r[3].Url));
        Assert.Equal(["one", "two"], r[4].Rows.Select(row => Assert.Single(row)));
        Assert.Empty(r[4].Columns);
        Assert.Equal(("An old-style book title", ""), (r[5].Title, r[5].Text));
    }

    [Fact]
    public void Resources_are_kept_within_their_limits_with_warnings()
    {
        var rows = new JsonArray(Enumerable.Range(1, 70).Select(i => (JsonNode?)new JsonArray($"w{i}", "x")).ToArray());
        var wide = new JsonArray((JsonNode?)new JsonArray(Enumerable.Range(1, 10).Select(i => (JsonNode?)$"c{i}").ToArray()));
        var step = new JsonObject
        {
            ["title"] = "S",
            ["resources"] = new JsonArray(
                new JsonObject { ["title"] = "Long", ["rows"] = rows },
                new JsonObject { ["title"] = "Bad link", ["url"] = "javascript:alert(1)" },
                new JsonObject { ["title"] = "Wide", ["rows"] = wide }),
        };
        var result = MissionImport.Parse(new JsonObject { ["title"] = "T", ["steps"] = new JsonArray(step) }.ToJsonString());

        Assert.True(result.Ok);
        var r = result.Draft!.Phases[0].Steps[0].Resources!;
        Assert.Equal(MissionLimits.ResourceRows, r[0].Rows.Count);
        Assert.Equal("", r[1].Url);
        Assert.Equal(MissionLimits.ResourceColumns, r[2].Rows[0].Count);
        Assert.Contains(result.Warnings, w => w.Contains("rows were left out"));
        Assert.Contains(result.Warnings, w => w.Contains("not a web address"));
        Assert.Contains(result.Warnings, w => w.Contains("more than 8 columns"));

        // The whole step keeps to its row budget.
        var many = Enumerable.Range(0, 4).Select(_ => new MissionResource { Title = "T", Rows = Enumerable.Range(0, 60).Select(i => (IReadOnlyList<string>)[$"r{i}"]).ToList() });
        Assert.Equal(MissionLimits.StepResourceRows, MissionResources.Clean(many).Sum(x => x.Rows.Count));
    }

    [Fact]
    public void A_step_keeps_details_for_new_versions_and_a_line_for_old_ones()
    {
        var id = _store.Create(MissionImport.Parse(WithResources).Draft!, MissionSource.Import);
        var step = _store.Steps(id)[0].Value;

        Assert.Equal(6, step.Materials.Count);
        Assert.Equal("Course book — https://example.com/book", step.Resources[0]);
        Assert.Equal("Words 1-3", step.Resources[1]);
        Assert.Equal("Test 1 — https://example.com/test", step.Resources[3]);
        Assert.Same(step.Materials, MissionResources.Of(step));

        // The record goes through sync as JSON, table and all.
        var json = JsonSerializer.Serialize(step, Helm.Core.Sync.SyncJson.Options);
        var back = JsonSerializer.Deserialize<MissionStep>(json, Helm.Core.Sync.SyncJson.Options)!;
        Assert.Equal(["附近", "fùjìn", "nearby"], back.Materials[1].Rows[1]);

        // An older Helm that edited the step dropped Materials: its lines are read instead.
        var old = step with { Materials = [] };
        var fromLines = MissionResources.Of(old);
        Assert.Equal(("Course book", "https://example.com/book"), (fromLines[0].Title, fromLines[0].Url));
        Assert.Equal("Words 1-3", fromLines[1].Title);

        // Plain links only: the record looks as it always did.
        var plain = _store.Steps(CreateHsk(start: false))[0].Value;
        Assert.Empty(plain.Materials);
        Assert.Equal(["https://example.com/hsk1"], plain.Resources);
    }

    [Fact]
    public void Resources_copy_as_json_and_as_text_and_reach_claude()
    {
        var id = _store.Create(MissionImport.Parse(WithResources).Draft!, MissionSource.Import);
        var mission = _store.GetMission(id)!;
        var steps = _store.Steps(id).Select(s => s.Value).ToList();

        var again = MissionImport.Parse(MissionsFormat.Json(mission, steps));
        Assert.True(again.Ok, string.Join("; ", again.Errors));
        Assert.Equal(
            MissionResources.Signature(steps[0].Materials),
            MissionResources.Signature(MissionResources.Clean(again.Draft!.Phases[0].Steps[0].Resources)));

        var text = MissionResources.ToText(steps[0].Materials[1]);
        Assert.Equal("Words 1-3 (Vocabulary)\n\nWord\tPinyin\tMeaning\n担心\tdānxīn\tto worry\n附近\tfùjìn\tnearby\n根据\tgēnjù\t\n", text);
    }

    [Fact]
    public async Task Claude_writes_and_reads_resources_with_their_tables()
    {
        var server = Server();
        var args = JsonNode.Parse(WithResources)!.AsObject();

        var created = await Call(server, "mission_create", args);
        Assert.False(created.IsError, created.Text);
        Assert.Contains("\"columns\":[\"Word\",\"Pinyin\",\"Meaning\"]", created.Text);
        Assert.Contains("\"label\":\"Mock test\"", created.Text);
        Assert.Contains("\"Course book — https://example.com/book\"", created.Text);
    }

    [Fact]
    public void The_page_shows_resources_opens_their_details_and_copies_them()
    {
        _store.Start(_store.Create(MissionImport.Parse(WithResources).Draft!, MissionSource.Import));
        var clipboard = new MemoryClipboard();
        var vm = ViewModel(clipboard);

        var words = vm.CurrentResources[1];
        Assert.Equal(("Vocabulary", "Words 1-3", "3 rows"), (words.Label, words.Title, words.Summary));
        Assert.Equal("Word · Pinyin · Meaning", words.Header);
        Assert.Equal(("担心", "dānxīn · to worry"), (words.Rows[0].First, words.Rows[0].Rest));
        Assert.False(words.ShowDetails);

        vm.ActivateResourceCommand.Execute(words);
        Assert.True(words.ShowDetails);
        vm.ActivateResourceCommand.Execute(words);
        Assert.False(words.ShowDetails);

        var link = vm.CurrentResources[0];
        Assert.True(link.IsPlainLink);
        Assert.Equal("example.com", link.Summary);

        vm.CopyResourceCommand.Execute(words);
        Assert.StartsWith("Words 1-3 (Vocabulary)", clipboard.Text);

        // The roadmap makes a step's resource rows only when the step is opened.
        var row = vm.Phases[0].Steps[0];
        Assert.True(row.HasResources);
        Assert.Empty(row.Resources);
        vm.ToggleStepCommand.Execute(row);
        Assert.Equal(6, row.Resources.Count);
    }

    // ---- Page ----------------------------------------------------------------------------------------------------

    private MissionsViewModel ViewModel(MemoryClipboard? clipboard = null, Helm.Shell.Services.IDialogService? dialogs = null, LinkHub? links = null,
        params ICaptureTarget[] targets) =>
        new(_store, _settings, new InlineUi(), dialogs ?? new AcceptDialogs(), clipboard ?? new MemoryClipboard(), new NullLauncher(),
            new MissionReminderService(_store, _settings, _time), () => targets, NullLogger<MissionsViewModel>.Instance, TimeZoneInfo.Utc, links);

    [Fact]
    public void The_page_copies_the_prompt_imports_the_answer_and_creates_the_mission()
    {
        var clipboard = new MemoryClipboard();
        var vm = ViewModel(clipboard);
        Assert.True(vm.HasNoMissions);

        vm.OpenPromptCommand.Execute(null);
        Assert.False(vm.CanCopyPrompt);
        vm.PromptGoal = "Pass HSK3";
        vm.CopyPromptCommand.Execute(null);
        Assert.Contains("My goal: Pass HSK3", clipboard.Text);

        vm.OpenImportCommand.Execute(null);
        vm.ImportText = "not json";
        Assert.True(vm.HasImportErrors);
        Assert.False(vm.CanCreate);
        vm.ImportText = Hsk;
        Assert.True(vm.HasPreview);
        Assert.Contains("2 phases · 4 steps · about 9 days", vm.PreviewSummary);
        // Leave out the mock test and rename it.
        vm.PreviewPhases[1].Steps[1].Include = false;
        Assert.Contains("3 steps", vm.PreviewSummary);
        vm.PreviewTitle = "HSK3 by March";
        vm.CreateMissionCommand.Execute(null);

        Assert.False(vm.IsNewOpen);
        var mission = Assert.Single(_store.Missions());
        Assert.Equal("HSK3 by March", mission.Value.Title);
        Assert.Equal(3, _store.Steps(mission.Id).Count);
        Assert.Equal(mission.Id, vm.SelectedMission?.Id);
        Assert.True(vm.IsPlanned);
        Assert.False(vm.ShowCurrentStep);
        // Before Start, no step is "now".
        Assert.All(vm.Phases.SelectMany(p => p.Steps), s => Assert.Equal(StepState.Upcoming, s.State));
    }

    [Fact]
    public async Task Completing_steps_on_the_page_moves_on_and_celebrates_phases_and_the_mission()
    {
        var id = CreateHsk();
        var vm = ViewModel();
        var played = new List<CelebrationKind?>();
        vm.Celebrated += (_, kind) => played.Add(kind);

        Assert.Equal(id, vm.SelectedMission?.Id);
        Assert.True(vm.ShowCurrentStep);
        Assert.Equal("HSK1 words", vm.CurrentTitle);
        Assert.Equal("Step 1 of 4", vm.CurrentNumberText);
        Assert.Equal(2, vm.CurrentChecklist.Count);
        Assert.True(vm.CurrentResources[0].IsLink);

        // Ticked on the page (a click, the keyboard or a screen reader all set IsDone).
        vm.CurrentChecklist[0].IsDone = true;
        Assert.True(_store.Steps(id)[0].Value.Checklist[0].IsDone);
        Assert.True(vm.CurrentChecklist[0].IsDone);
        vm.CurrentChecklist[1].IsDone = true;
        vm.CurrentChecklist[1].IsDone = false;
        Assert.False(_store.Steps(id)[0].Value.Checklist[1].IsDone);
        vm.CompleteNote = "first!";
        await vm.CompleteStepCommand.ExecuteAsync(null);
        Assert.Equal("HSK1 review", vm.CurrentTitle);
        Assert.Equal("", vm.CompleteNote);
        Assert.Contains("Step 1 of 4 done", vm.Message);

        await vm.CompleteStepCommand.ExecuteAsync(null);
        Assert.Equal(CelebrationKind.Phase, vm.Celebration?.Kind);
        Assert.Equal("Hotpot", vm.Celebration?.Reward);
        vm.ClaimCelebrationRewardCommand.Execute(null);
        Assert.NotNull(_store.GetMission(id)!.Phases[0].RewardClaimedAt);
        vm.DismissCelebrationCommand.Execute(null);

        await vm.CompleteStepCommand.ExecuteAsync(null);
        await vm.CompleteStepCommand.ExecuteAsync(null);
        Assert.True(vm.IsCompleted);
        Assert.True(vm.IsMissionCelebration);
        Assert.Equal([null, CelebrationKind.Phase, null, CelebrationKind.Mission], played);
        Assert.Equal("All 4 steps done", vm.ProgressText);
        Assert.Equal(["✓", "✓", "✓", "✓"], vm.Phases.SelectMany(p => p.Steps).Select(s => s.Marker));
    }

    [Fact]
    public async Task A_mission_finished_elsewhere_is_celebrated_once_on_this_device()
    {
        var id = CreateHsk();
        var vm = ViewModel();
        vm.ConfirmUntickedChecklist = false;
        await vm.CompleteStepCommand.ExecuteAsync(null);
        await vm.CompleteStepCommand.ExecuteAsync(null);
        Assert.Equal(CelebrationKind.Phase, vm.Celebration?.Kind); // still open
        // Claude (or the phone) completes the rest.
        _store.Complete(id);
        _store.Complete(id);

        Assert.True(vm.IsMissionCelebration);
        Assert.Null(vm.Message);
        vm.DismissCelebrationCommand.Execute(null);
        vm.Refresh();
        Assert.False(vm.HasCelebration);
        Assert.False(ViewModel().HasCelebration);
    }

    [Fact]
    public void The_roadmap_shows_each_step_state_and_edits_steps_not_done_yet()
    {
        var id = CreateHsk();
        _store.Complete(id);
        var vm = ViewModel();

        var rows = vm.Phases.SelectMany(p => p.Steps).ToList();
        Assert.Equal([StepState.Done, StepState.Current, StepState.Upcoming, StepState.Upcoming], rows.Select(r => r.State));
        Assert.False(rows[0].CanEdit);
        Assert.True(rows[2].CanDelete);
        Assert.False(rows[1].CanDelete);

        vm.EditStepCommand.Execute(rows[2]);
        rows[2].EditTitle = "HSK2 words, all 300";
        rows[2].EditDays = "4,5";
        vm.SaveStepCommand.Execute(rows[2]);
        Assert.Equal("HSK2 words, all 300", _store.Steps(id)[2].Value.Title);
        Assert.Equal(4.5, _store.Steps(id)[2].Value.EstimateDays);
        Assert.Same(rows[2], vm.Phases[1].Steps[0]); // the same row object, updated in place

        vm.NewStepTitle = "Real exam";
        vm.AddStepCommand.Execute(null);
        Assert.Equal("Real exam", vm.Phases[1].Steps[^1].Title);
    }

    [Fact]
    public async Task Completing_with_unticked_items_asks_first()
    {
        var id = CreateHsk();
        var vm = ViewModel(dialogs: new RefuseDialogs());

        await vm.CompleteStepCommand.ExecuteAsync(null);
        Assert.Equal("HSK1 words", vm.CurrentTitle);

        vm.ConfirmUntickedChecklist = false;
        await vm.CompleteStepCommand.ExecuteAsync(null);
        Assert.Equal("HSK1 review", vm.CurrentTitle);
        Assert.NotNull(_store.Steps(id)[0].Value.CompletedAt);
    }

    // ---- MCP -----------------------------------------------------------------------------------------------------

    private McpServer Server(bool readOnly = false) =>
        new(new MissionsMcpTools(_store).Tools.Where(t => !readOnly || t.ReadOnly), "0.21.0", McpEndpoint.Instructions);

    [Fact]
    public async Task Claude_creates_a_mission_reads_it_and_completes_the_current_step()
    {
        var server = Server();
        // The same JSON the import reads, as tool arguments.
        var mission = JsonNode.Parse(MissionImport.Extract(Hsk, out _)!, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true })!.AsObject();
        mission["start"] = true;

        var created = await Call(server, "mission_create", mission);
        Assert.False(created.IsError, created.Text);
        var id = Assert.Single(_store.Missions()).Id;
        Assert.Equal(MissionSource.Claude, _store.GetMission(id)!.Source);
        Assert.Equal(MissionStatus.Active, _store.GetMission(id)!.Status);

        var list = await Call(server, "missions_list", new JsonObject());
        Assert.Contains("\"current_step\":\"HSK1 words\"", list.Text);

        var done = await Call(server, "mission_complete_step", new JsonObject { ["mission"] = "pass hsk3", ["note"] = "via Claude" });
        Assert.False(done.IsError, done.Text);
        Assert.Contains("\"next_step\":\"HSK1 review\"", done.Text);
        Assert.Equal("via Claude", _store.Steps(id)[0].Value.Note);

        var read = await Call(server, "mission_get", new JsonObject { ["mission"] = id });
        Assert.Contains("\"state\":\"done\"", read.Text);
        Assert.Contains("\"done_when\":\"90% on a self-test\"", read.Text);
    }

    [Fact]
    public async Task Claude_gets_errors_it_can_act_on()
    {
        var server = Server();

        var bad = await Call(server, "mission_create", new JsonObject { ["title"] = "X", ["phases"] = new JsonArray(new JsonObject { ["title"] = "A", ["steps"] = new JsonArray(new JsonObject { ["description"] = "?" }) }) });
        Assert.True(bad.IsError);
        Assert.Contains("Phase 1, step 1 has no title", bad.Text);

        var id = CreateHsk(start: false);
        var notStarted = await Call(server, "mission_complete_step", new JsonObject { ["mission"] = id });
        Assert.True(notStarted.IsError);
        Assert.Contains("has not been started", notStarted.Text);

        var unknown = await Call(server, "mission_get", new JsonObject { ["mission"] = "nope" });
        Assert.True(unknown.IsError);
        Assert.Contains("Pass HSK3", unknown.Text);

        Assert.Equal(["mission_get", "missions_list"], Server(readOnly: true).ToolNames.OrderBy(n => n));
        Assert.Contains("mission_replan", Server().ToolNames);
    }

    // ---- v2: planning the rest again -----------------------------------------------------------------------------

    private static MissionDraft Plan(DateOnly? deadline = null, string note = "", params (string Phase, string[] Steps)[] phases) =>
        new("", "", note, "", deadline, phases.Select(p => new PhaseDraft(p.Phase, "", p.Steps.Select(s => new StepDraft(s)).ToList())).ToList());

    [Fact]
    public void Replan_keeps_the_done_steps_and_replaces_the_rest()
    {
        var id = CreateHsk();
        _store.Complete(id, "done");
        var foundations = _store.GetMission(id)!.Phases[0].Key;

        Assert.True(_store.Replan(id, Plan(new DateOnly(2027, 4, 1), "Slower than planned",
            ("foundations", ["HSK1 review again"]), ("HSK2 + HSK3", ["HSK2 words", "HSK3 words"]))));

        var mission = _store.GetMission(id)!;
        var steps = _store.Steps(id).Select(s => s.Value).ToList();
        Assert.Equal(["HSK1 words", "HSK1 review again", "HSK2 words", "HSK3 words"], steps.Select(s => s.Title));
        Assert.Equal(["Foundations", "HSK2 + HSK3"], mission.Phases.Select(p => p.Title)); // the old "HSK2" phase is gone
        Assert.Equal(foundations, mission.Phases[0].Key);
        Assert.Equal([foundations, foundations], steps.Take(2).Select(s => s.PhaseKey));
        Assert.True(steps[0].IsDone);
        Assert.Equal("done", steps[0].Note);
        Assert.Equal((new DateOnly(2027, 4, 1), "Slower than planned", "Pass HSK3"), (mission.Deadline, mission.Note, mission.Title));
        Assert.Equal("HSK1 review again", _store.CurrentStep(id)!.Value.Title);
        Assert.Equal(MissionStatus.Active, mission.Status);
    }

    [Fact]
    public void A_finished_mission_or_one_too_big_cannot_be_planned_again()
    {
        var id = CreateHsk();
        for (var i = 0; i < 4; i++) _store.Complete(id);
        Assert.False(_store.Replan(id, Plan(phases: ("More", ["x"]))));

        var other = CreateHsk();
        var tooMany = Enumerable.Range(0, MissionLimits.Steps).Select(i => $"Step {i}").ToArray();
        _store.Complete(other);
        Assert.False(_store.Replan(other, Plan(phases: ("Big", tooMany))));
        Assert.Equal(4, _store.Steps(other).Count);
    }

    [Fact]
    public void The_replan_prompt_carries_the_progress_and_its_answer_needs_no_title()
    {
        var id = CreateHsk();
        _time.Advance(TimeSpan.FromDays(4));
        _store.Complete(id, "Anki done");
        var mission = _store.GetMission(id)!;
        var steps = _store.Steps(id).Select(s => s.Value).ToList();

        var prompt = MissionPrompt.BuildReplan(mission, steps, MissionPace.Compute(mission, steps, [], _store.Now, TimeZoneInfo.Utc),
            new DateOnly(2026, 10, 5), "I was ill for a week");

        Assert.Contains("Mission: Pass HSK3", prompt);
        Assert.Contains("1 of 4 steps done in 4 days, on track", prompt);
        Assert.Contains("- HSK1 words (took 4 days, planned 3): Anki done", prompt);
        Assert.Contains("What changed, or what I want: I was ill for a week", prompt);
        Assert.Contains("\"title\": \"HSK1 review\"", prompt);
        Assert.DoesNotContain("\"title\": \"HSK1 words\"", prompt);

        var answer = "```json\n{ \"phases\": [ { \"title\": \"HSK2\", \"steps\": [ { \"title\": \"HSK2 words\" } ] } ] }\n```";
        Assert.False(MissionImport.Parse(answer).Ok);
        Assert.True(MissionImport.Parse(answer, requireTitle: false).Ok);
    }

    [Fact]
    public void The_page_plans_the_rest_again_from_an_ai_answer()
    {
        var id = CreateHsk();
        _store.Complete(id);
        var clipboard = new MemoryClipboard();
        var vm = ViewModel(clipboard);

        vm.OpenReplanCommand.Execute(null);
        Assert.True(vm.IsReplanOpen);
        vm.ReplanReason = "Tôi bận cả tuần";
        vm.CopyReplanPromptCommand.Execute(null);
        Assert.Contains("What changed, or what I want: Tôi bận cả tuần", clipboard.Text);

        vm.OpenReplanImportCommand.Execute(null);
        Assert.True(vm.IsImportOpen && vm.IsReplan && !vm.IsNewMission);
        Assert.Equal("Replace the remaining steps", vm.CreateLabel);
        vm.ImportText = "{ \"phases\": [ { \"title\": \"HSK2\", \"steps\": [ \"HSK2 words\", \"HSK2 mock test\" ] } ] }";
        Assert.True(vm.CanCreate);
        Assert.StartsWith("Replaces the 3 steps not done yet", vm.PreviewSummary);
        Assert.Equal(new DateTime(2027, 3, 1), vm.PreviewDeadline); // the mission's own deadline is kept

        vm.CreateMissionCommand.Execute(null);

        Assert.False(vm.IsNewOpen);
        Assert.False(vm.IsReplan);
        Assert.Equal(["HSK1 words", "HSK2 words", "HSK2 mock test"], _store.Steps(id).Select(s => s.Value.Title));
        Assert.Single(_store.Missions());
        Assert.Equal("HSK2 words", vm.CurrentTitle);
        Assert.Contains("2 steps to go", vm.Message);
    }

    // ---- v2: share, reminders, Notes ------------------------------------------------------------------------------

    [Fact]
    public void A_shared_ai_answer_with_a_mission_becomes_a_new_mission()
    {
        var target = new MissionCaptureTarget(_store);

        Assert.True(target.Claims(Hsk));
        Assert.False(target.Claims("https://youtu.be/dQw4w9WgXcQ"));
        Assert.False(target.Claims("{ \"steps\": \"nope\" }"));
        Assert.Equal("New mission “Pass HSK3”: 2 phases, 4 steps, about 9 days", target.Preview(Hsk).Text);
        Assert.False(target.Preview("buy milk").CanSave);

        var result = target.Capture(Hsk);

        Assert.True(result.Saved);
        var mission = Assert.Single(_store.Missions()).Value;
        Assert.Equal((MissionStatus.Planned, MissionSource.Import), (mission.Status, mission.Source));
    }

    [Fact]
    public void The_daily_reminder_names_each_mission_s_step_once_a_day_and_skips_missions_with_progress_today()
    {
        var reminders = new MissionReminderService(_store, _settings, _time);
        reminders.Settings.Update(s => s.ReminderHour = 9);
        var id = CreateHsk();
        CreateHsk(start: false); // planned: no reminder

        Assert.Null(reminders.TakeDue(TimeZoneInfo.Utc)); // 08:00, before the hour
        _time.Advance(TimeSpan.FromHours(2));
        var reminder = reminders.TakeDue(TimeZoneInfo.Utc);
        Assert.Equal(("Today's step", "Pass HSK3: HSK1 words"), (reminder?.Title, reminder?.Message));
        Assert.Null(reminders.TakeDue(TimeZoneInfo.Utc)); // once a day

        _time.Advance(TimeSpan.FromDays(1));
        _store.Complete(id);
        Assert.Null(reminders.TakeDue(TimeZoneInfo.Utc)); // progress today: nothing to nudge
        Assert.True(reminders.RemindNow(TimeZoneInfo.Utc)); // "Remind me now" shows it anyway

        reminders.Settings.Update(s => s.RemindersEnabled = false);
        _time.Advance(TimeSpan.FromDays(1));
        Assert.Null(reminders.TakeDue(TimeZoneInfo.Utc));
    }

    [Fact]
    public void The_summary_is_saved_to_notes_when_notes_is_there()
    {
        var id = CreateHsk();
        Assert.False(ViewModel().CanSaveToNotes); // no Notes

        var notes = new RecordingNotes();
        var vm = ViewModel(targets: notes);
        Assert.False(vm.CanSaveToNotes); // nothing done yet
        _store.Complete(id, "Anki done");
        Assert.True(vm.CanSaveToNotes);

        vm.SaveSummaryToNotesCommand.Execute(null);

        Assert.StartsWith("Mission: Pass HSK3\n", notes.Captured);
        Assert.Contains("- ✓ HSK1 words", notes.Captured);
        Assert.DoesNotContain("# Mission", notes.Captured);
        Assert.Equal("Saved to Notes.", vm.Message);

        _settings.Get<GeneralSettings>(GeneralSettings.StoreId).Update(s => s.EnabledModules["notes"] = false);
        vm.Refresh();
        Assert.False(vm.CanSaveToNotes);
    }

    [Fact]
    public async Task Claude_plans_the_rest_of_a_mission_again()
    {
        var server = Server();
        var id = CreateHsk();
        _store.Complete(id);

        var replanned = await Call(server, "mission_replan", new JsonObject
        {
            ["mission"] = "Pass HSK3",
            ["deadline"] = "2027-05-01",
            ["phases"] = new JsonArray(new JsonObject { ["title"] = "Foundations", ["steps"] = new JsonArray(new JsonObject { ["title"] = "Review, slower" }) }),
        });

        Assert.False(replanned.IsError, replanned.Text);
        Assert.Equal(["HSK1 words", "Review, slower"], _store.Steps(id).Select(s => s.Value.Title));
        Assert.Equal(new DateOnly(2027, 5, 1), _store.GetMission(id)!.Deadline);

        _store.Complete(id);
        var finished = await Call(server, "mission_replan", new JsonObject { ["mission"] = id, ["phases"] = new JsonArray(new JsonObject { ["title"] = "X", ["steps"] = new JsonArray("y") }) });
        Assert.True(finished.IsError);
        Assert.Contains("nothing left to plan", finished.Text);
    }

    // ---- v3: badges ----------------------------------------------------------------------------------------------

    private IReadOnlyDictionary<string, DateTimeOffset?> Badges() =>
        MissionBadges.Compute(_store, TimeZoneInfo.Utc).ToDictionary(b => b.Id, b => b.EarnedAt);

    [Fact]
    public void Badges_are_earned_from_the_history_at_the_moment_they_were_met()
    {
        Assert.All(Badges().Values, Assert.Null);
        var id = CreateHsk(); // deadline 2027-03-01, estimates 3 + 1 + 4 + 1 days

        for (var day = 0; day < 4; day++)
        {
            _time.Advance(TimeSpan.FromDays(1));
            _store.Complete(id);
        }

        var badges = Badges();
        Assert.Equal(T0.AddDays(1), badges["first-step"]);
        Assert.Equal(T0.AddDays(2), badges["phase-1"]);
        Assert.Equal(T0.AddDays(3), badges["streak-3"]); // days 1, 2, 3
        Assert.Equal(T0.AddDays(4), badges["mission-1"]);
        Assert.Equal(T0.AddDays(4), badges["ahead"]); // 4 days against 9 planned
        Assert.Equal(T0.AddDays(4), badges["deadline"]);
        Assert.Null(badges["streak-7"]);
        Assert.Null(badges["steps-10"]);
        Assert.Null(badges["comeback"]);
        Assert.Equal(MissionBadges.Count, badges.Count);
    }

    [Fact]
    public void A_reopened_step_does_not_count_twice_and_a_comeback_needs_a_step_after_resuming()
    {
        var id = CreateHsk();
        _store.Complete(id);
        _store.ReopenLast(id);
        _store.Complete(id);
        Assert.Single(_store.History(id), e => e.Kind == MissionEventKind.StepReopened);

        _store.Pause(id);
        _store.Resume(id);
        Assert.Null(Badges()["comeback"]);
        _time.Advance(TimeSpan.FromHours(1));
        _store.Complete(id);
        Assert.Equal(T0.AddHours(1), Badges()["comeback"]);
    }

    [Fact]
    public async Task The_page_lists_the_badges_and_names_a_new_one_when_a_step_earns_it()
    {
        CreateHsk();
        var vm = ViewModel();
        vm.ConfirmUntickedChecklist = false;
        Assert.Equal($"0 of {MissionBadges.Count} earned", vm.BadgesSummary);
        Assert.All(vm.Badges, b => Assert.True(b.Locked));

        await vm.CompleteStepCommand.ExecuteAsync(null);

        Assert.Contains("New badge: First step!", vm.Message);
        Assert.Equal($"1 of {MissionBadges.Count} earned", vm.BadgesSummary);
        Assert.StartsWith("Earned ", vm.Badges[0].Detail);

        await vm.CompleteStepCommand.ExecuteAsync(null); // finishes the first phase
        Assert.Contains("New badge: Phase cleared!", vm.Celebration!.Stats);
    }

    // ---- v3: links -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Notes_link_to_missions_and_the_mission_page_shows_them()
    {
        var notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        var opened = new List<string>();
        var hub = new LinkHub(new MemorySynced<HelmLink>(), () =>
        [
            new NoteLinkProvider(notes, id => opened.Add("note " + id)),
            new MissionLinkProvider(_store, id => opened.Add("mission " + id)),
        ], _time);
        var id = CreateHsk();
        var provider = hub.Provider(LinkKinds.Mission)!;

        Assert.Equal(("Pass HSK3", "Mission · step 1 of 4"), (provider.Resolve(id)!.Title, provider.Resolve(id)!.Subtitle));
        Assert.Equal(id, Assert.Single(provider.Search("hsk", 5)).Ref.Id);
        Assert.Empty(provider.Search("marathon", 5));
        Assert.Null(provider.Resolve("gone"));
        provider.Open(id);
        Assert.Equal(["mission " + id], opened);

        var vm = ViewModel(links: hub);
        Assert.NotNull(vm.MissionLinks);
        Assert.Empty(vm.MissionLinks!.Items);
        var tips = notes.Add("Vocab tips", "Learn in context");
        hub.Link(new LinkRef(LinkKinds.Note, tips), new LinkRef(LinkKinds.Mission, id));
        Assert.Equal("Vocab tips", Assert.Single(vm.MissionLinks.Items).Title);

        // Claude links a note to the mission too.
        var server = new McpServer(new Helm.Modules.Notes.NotesMcpTools(notes, hub).Tools, "0.22.0", McpEndpoint.Instructions);
        var exam = notes.Add("Exam day", "Bring ID");
        var linked = await Call(server, "notes_link", new JsonObject { ["id"] = exam, ["mission_id"] = id });
        Assert.False(linked.IsError, linked.Text);
        Assert.Equal(2, vm.MissionLinks.Items.Count);
        Assert.True((await Call(server, "notes_link", new JsonObject { ["id"] = exam, ["mission_id"] = "nope" })).IsError);

        // Without Notes there is nothing to link.
        Assert.Null(ViewModel().MissionLinks);
    }

    // ---- v3: widget ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_widget_shows_the_missions_in_progress_with_their_step_and_progress()
    {
        Assert.True(MissionWidgetModel.Build(_store, _store.Now, TimeZoneInfo.Utc).IsEmpty);
        var planned = CreateHsk(start: false);
        Assert.Contains("press Start mission", MissionWidgetModel.Build(_store, _store.Now, TimeZoneInfo.Utc).EmptyText);

        var paused = CreateHsk();
        _store.Pause(paused);
        var active = CreateHsk();
        _store.Complete(active);
        _time.Advance(TimeSpan.FromDays(6)); // step 2 was due by day 4

        var model = MissionWidgetModel.Build(_store, _store.Now, TimeZoneInfo.Utc);

        Assert.Equal([active, paused], model.Rows.Select(r => r.MissionId)); // in progress first
        Assert.Equal(("HSK1 review", "1/4 · 2 days behind", true), (model.Rows[0].Step, model.Rows[0].Progress, model.Rows[0].IsBehind));
        Assert.Equal(("HSK1 words", "0/4 · paused", false), (model.Rows[1].Step, model.Rows[1].Progress, model.Rows[1].IsBehind));
        Assert.DoesNotContain(model.Rows, r => r.MissionId == planned);
        Assert.Equal(0, model.More);
    }

    // ---- v4: templates, statistics, Tracker, Today -----------------------------------------------------------------

    [Fact]
    public void Every_template_imports_cleanly()
    {
        Assert.NotEmpty(MissionTemplates.All);
        Assert.Equal(MissionTemplates.All.Count, MissionTemplates.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in MissionTemplates.All)
        {
            var result = MissionImport.Parse(t.Json);
            Assert.True(result.Ok, $"{t.Id}: {string.Join("; ", result.Errors)}");
            Assert.Empty(result.Warnings);
            Assert.True(result.Draft!.Phases.Count >= 3, t.Id);
        }
    }

    [Fact]
    public void A_template_opens_the_import_preview()
    {
        var vm = ViewModel();
        vm.UseTemplateCommand.Execute(MissionTemplates.All[0]);

        Assert.True(vm.IsImportOpen);
        Assert.True(vm.HasPreview);
        Assert.Equal("Run 5 km", vm.PreviewTitle);
    }

    [Fact]
    public void Statistics_count_steps_per_week_the_pace_and_missions_by_their_deadline()
    {
        // T0 is Thursday 1 October 2026.
        var id = CreateHsk();
        _time.Advance(TimeSpan.FromDays(6)); // Wednesday 7 October: the first step took 6 days for 3 planned
        _store.Complete(id);
        _store.Skip(id); // skipped steps count in neither
        _time.Advance(TimeSpan.FromDays(7)); // Wednesday 14 October
        _store.Complete(id);
        _store.Complete(id);

        var stats = MissionStats.Compute(_store, _store.Now, TimeZoneInfo.Utc);

        Assert.Equal([0, 0, 1, 2], stats.StepsPerWeek);
        Assert.Equal((2, 3), (stats.StepsThisWeek, stats.StepsDone));
        Assert.Equal((1, 1, 1, 0), (stats.MissionsDone, stats.MissionsWithDeadline, stats.MissionsOnTime, stats.MissionsRunning));
        Assert.Equal((6 + 7 + 0) / (3 + 4 + 1.0), stats.PaceRatio!.Value, 3);
        Assert.StartsWith("Steps take 1.6", MissionStats.PaceText(stats).Replace(',', '.'));
    }

    [Fact]
    public void The_csv_has_every_step_with_its_status_and_quotes_what_needs_it()
    {
        var id = CreateHsk();
        _time.Advance(TimeSpan.FromDays(2));
        _store.Complete(id, "Anki, 94%");

        var csv = MissionStats.Csv(_store).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("mission,mission_status,phase,step_number,step,status,planned_days", csv[0]);
        Assert.Equal(5, csv.Length);
        Assert.StartsWith("Pass HSK3,active,Foundations,1,HSK1 words,done,3,", csv[1]);
        Assert.Contains(",2,90% on a self-test,\"Anki, 94%\",", csv[1]);
        Assert.StartsWith("Pass HSK3,active,Foundations,2,HSK1 review,current,1,", csv[2]);
        Assert.Contains(",open,", csv[3]);
    }

    [Fact]
    public void A_step_sent_to_tracker_and_its_task_finish_together()
    {
        var tracker = new Helm.Modules.Tracker.TrackerStore(new MemorySynced<Helm.Modules.Tracker.TrackerWorkspace>(), new MemorySynced<Helm.Modules.Tracker.TrackerItem>(),
            new MemorySyncedLog<Helm.Modules.Tracker.TrackerEvent>(), _time);
        var bridge = new Helm.Modules.Tracker.TrackerTaskBridge(tracker);
        var sync = new MissionTaskSync(_store, () => [bridge], _settings);
        var id = CreateHsk();

        Assert.Equal("Tracker", sync.Send(id, TimeZoneInfo.Utc));
        var step = _store.Steps(id)[0].Value;
        var task = tracker.GetItem(step.TaskId!)!;
        Assert.Equal(("HSK1 words", new DateOnly(2026, 10, 4)), (task.Title, task.DueDate)); // due when the 3-day step should end
        Assert.Contains("Mission: Pass HSK3", task.Notes);
        Assert.Equal(Helm.Modules.Tracker.TrackerTaskBridge.DefaultListName, tracker.Workspaces().Single().Value.Name);
        Assert.Equal("Tracker", sync.Send(id, TimeZoneInfo.Utc)); // sent already: no second task
        Assert.Single(tracker.AllItems());

        // Done in Tracker: the step is done here.
        tracker.Complete(step.TaskId!);
        Assert.True(_store.Steps(id)[0].Value.IsDone);
        Assert.Equal("Done in Tracker.", _store.Steps(id)[0].Value.Note);

        // Done here: the task is done there.
        sync.Send(id, TimeZoneInfo.Utc);
        var second = _store.Steps(id)[1].Value.TaskId!;
        _store.Complete(id);
        Assert.True(tracker.GetItem(second)!.IsCompleted);

        // Tracker off: nothing to send to.
        _settings.Get<GeneralSettings>(GeneralSettings.StoreId).Update(s => s.EnabledModules["tracker"] = false);
        Assert.Null(sync.Bridge);
        Assert.Null(sync.Send(id, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task The_today_card_lists_each_running_mission_and_completes_its_step()
    {
        var first = CreateHsk();
        var vm = ViewModel();
        vm.ConfirmUntickedChecklist = false;
        Assert.False(vm.HasToday); // one mission: its own card is enough
        var second = CreateHsk();
        _store.Complete(second);

        Assert.True(vm.HasToday);
        Assert.Equal([first, second], vm.Today.Select(r => r.MissionId).OrderBy(x => x));
        var row = vm.Today.Single(r => r.MissionId == second);
        Assert.Equal("HSK1 review", row.Step);
        Assert.StartsWith("Step 2 of 4", row.Details);

        await vm.CompleteTodayCommand.ExecuteAsync(row);

        Assert.Equal(second, vm.SelectedMission?.Id);
        Assert.Equal(CelebrationKind.Phase, vm.Celebration?.Kind);
        Assert.Equal("HSK2 words", vm.Today.Single(r => r.MissionId == second).Step);
        Assert.Equal("2 steps this week", vm.StatsThisWeek);
    }

    private sealed class RecordingNotes : ICaptureTarget
    {
        public string Captured { get; private set; } = "";
        public string Id => "note";
        public string ModuleId => "notes";
        public string Name => "Note";
        public string Prefix => "n";
        public string Example => "";
        public int Order => 0;
        public CapturePreview Preview(string text) => new(true, "");

        public CaptureResult Capture(string text)
        {
            Captured = text;
            return new(true, "Saved to Notes.");
        }
    }

    private static async Task<(bool IsError, string Text)> Call(McpServer server, string tool, JsonObject args)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
        };
        var reply = await server.HandleAsync(request.ToJsonString(), default);
        var result = reply!["result"]!;
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }

    private sealed class RefuseDialogs : Helm.Shell.Services.IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);
    }

    private sealed class NullLauncher : IProcessLauncher
    {
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void StartNewInstance(string? arguments = null) { }
    }
}
