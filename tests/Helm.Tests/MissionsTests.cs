using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Missions;
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
        Assert.Equal(["https://example.com/hsk1"], first.Resources!);
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

    // ---- Page ----------------------------------------------------------------------------------------------------

    private MissionsViewModel ViewModel(MemoryClipboard? clipboard = null, Helm.Shell.Services.IDialogService? dialogs = null) =>
        new(_store, _settings, new InlineUi(), dialogs ?? new AcceptDialogs(), clipboard ?? new MemoryClipboard(), new NullLauncher(),
            NullLogger<MissionsViewModel>.Instance, TimeZoneInfo.Utc);

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
