using Helm.Core.Ai;
using Helm.Core.Settings;
using Helm.Modules.Notes;
using Helm.Modules.Tracker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The views' glue for AI planning: "Split into steps" on a task (steps as its checklist, due days spread before the
/// task's, Undo) and "Make tasks" on a note (one list for all, the lines it skipped, "→ Tracker" tags and Undo).
/// </summary>
public sealed class PlanViewsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 3);
    private readonly ManualTime _time = new(T0);
    private readonly TrackerStore _tracker;
    private readonly NotesStore _notes;
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public PlanViewsTests()
    {
        _tracker = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
        _notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    private const string FiveSteps = """
        {"tasks": [{"title": "Học 10 câu mẫu part 1"}, {"title": "Mô tả 3 bức ảnh part 2"}, {"title": "So sánh 2 ảnh part 3"},
                   {"title": "Nói 2 phút part 4"}, {"title": "Ghi âm, nghe lại, sửa lỗi"}]}
        """;

    // ---- Split into steps -----------------------------------------------------------------------------------------

    [Fact]
    public void Steps_are_spread_evenly_before_the_task_and_never_on_or_after_it()
    {
        Assert.Equal([new(2026, 11, 4), new(2026, 11, 6), new(2026, 11, 8), new(2026, 11, 10), new DateOnly(2026, 11, 12)],
            TaskProposals.SpreadBefore(Today, new DateOnly(2026, 11, 14), 5));
        // More steps than days: they share the days, in order.
        Assert.Equal([new(2026, 11, 4), new(2026, 11, 4), new DateOnly(2026, 11, 5)], TaskProposals.SpreadBefore(Today, new DateOnly(2026, 11, 6), 3));
        // Due tomorrow: all today; overdue: all on its day.
        Assert.All(TaskProposals.SpreadBefore(Today, Today.AddDays(1), 3), d => Assert.Equal(Today, d));
        Assert.All(TaskProposals.SpreadBefore(Today, Today.AddDays(-2), 2), d => Assert.Equal(Today.AddDays(-2), d));
        Assert.Empty(TaskProposals.SpreadBefore(Today, Today.AddDays(9), 0));
    }

    [Fact]
    public async Task Kept_steps_become_the_tasks_checklist_as_edited_with_spread_days_and_Undo_takes_them_back()
    {
        var list = _tracker.AddWorkspace("Aptis B1");
        var task = _tracker.AddItem(list, new TrackerItemDraft("Ôn Speaking", DueDate: new DateOnly(2026, 11, 14)));
        var proposals = new TrackerProposalViewModel(_tracker, new ScriptedAiText(FiveSteps), _time);

        await proposals.ForTaskAsync(task);

        Assert.True(proposals.IsProposing);
        Assert.True(proposals.CanSpread);
        Assert.True(proposals.SpreadDates); // on by default: the task has a due date
        Assert.Equal($"Spread due dates before {TaskProposals.DayText(new DateOnly(2026, 11, 14))}", proposals.SpreadText);
        Assert.Equal("Add selected (5)", proposals.AddText);

        proposals.Choices[1].Keep = false;
        proposals.Choices[0].Title = "  Học 10 câu trả lời mẫu part 1 ";
        Assert.Equal("4 of 5 ticked", proposals.TickedText);
        Assert.Equal("", proposals.Choices[1].DueText);
        // Four steps now share the eleven days.
        Assert.Equal(TaskProposals.DayText(new DateOnly(2026, 11, 5)), proposals.Choices[0].DueText);
        Assert.Equal(TaskProposals.DayText(new DateOnly(2026, 11, 11)), proposals.Choices[4].DueText);

        proposals.AddCommand.Execute(null);

        var steps = _tracker.Subtasks(task);
        Assert.Equal(["Học 10 câu trả lời mẫu part 1", "So sánh 2 ảnh part 3", "Nói 2 phút part 4", "Ghi âm, nghe lại, sửa lỗi"], steps.Select(s => s.Value.Title));
        Assert.Equal([new(2026, 11, 5), new(2026, 11, 7), new(2026, 11, 9), new DateOnly(2026, 11, 11)], steps.Select(s => s.Value.DueDate!.Value));
        Assert.True(proposals.CanUndo);
        Assert.False(proposals.IsProposing);
        Assert.Equal("Added 4 steps to this task's checklist.", proposals.Status);

        proposals.UndoCommand.Execute(null);
        Assert.Empty(_tracker.Subtasks(task));
        Assert.False(proposals.IsOpen);
    }

    [Fact]
    public async Task Without_spreading_or_a_due_date_steps_keep_the_days_AI_gave()
    {
        var list = _tracker.AddWorkspace("Việc");
        var task = _tracker.AddItem(list, new TrackerItemDraft("Dọn nhà"));
        var proposals = new TrackerProposalViewModel(_tracker, new ScriptedAiText("""{"tasks": [{"title": "Lau bếp", "due": "2026-11-05"}, {"title": "Giặt rèm"}]}"""), _time);

        await proposals.ForTaskAsync(task);
        Assert.False(proposals.CanSpread);
        Assert.False(proposals.SpreadDates);
        proposals.AddCommand.Execute(null);

        Assert.Equal([new DateOnly(2026, 11, 5), null], _tracker.Subtasks(task).Select(s => s.Value.DueDate));
    }

    [Fact]
    public async Task While_AI_works_the_panel_says_where_and_a_problem_offers_Try_again()
    {
        var list = _tracker.AddWorkspace("Việc");
        var task = _tracker.AddItem(list, new TrackerItemDraft("Chuẩn bị thi"));
        var ai = new GatedAiText("Sonnet 5.5 · agent on HUNG-PC · 0đ");
        var proposals = new TrackerProposalViewModel(_tracker, ai, _time);

        var asking = proposals.ForTaskAsync(task);
        Assert.True(proposals.IsBusy);
        Assert.Equal("Sonnet 5.5 · agent on HUNG-PC · 0đ", proposals.Status);
        ai.Answer(AiTextAnswer.Failed("No Helm PC answered in time."));
        await asking;
        Assert.True(proposals.IsStuck);
        Assert.Equal("No Helm PC answered in time.", proposals.Status);

        var again = proposals.RetryCommand.ExecuteAsync(null);
        ai.Answer(new AiTextAnswer(FiveSteps, null, "Claude Code trên HUNG-PC"));
        await again;
        Assert.True(proposals.IsProposing);
        Assert.Equal(5, proposals.Choices.Count);
        Assert.Contains("Claude Code trên HUNG-PC", proposals.Status);
    }

    [Fact]
    public async Task The_proposals_show_under_their_task_only_and_its_button_hides_meanwhile()
    {
        var vm = new TrackerViewModel(_tracker, _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(),
            new TrackerReminderService(_tracker, _settings, _time), NullLogger<TrackerViewModel>.Instance,
            proposals: new TrackerProposalViewModel(_tracker, new ScriptedAiText(FiveSteps), _time));
        vm.CreateTasksWorkspaceCommand.Execute(null);
        vm.NewTitle = "Ôn Speaking";
        vm.AddCommand.Execute(null);
        vm.NewTitle = "Viết email";
        vm.AddCommand.Execute(null);
        var speaking = vm.OpenItems.Single(r => r.Title == "Ôn Speaking");
        var email = vm.OpenItems.Single(r => r.Title == "Viết email");
        Assert.True(speaking.CanBreakDown);

        await speaking.BreakDownCommand.ExecuteAsync(null);

        Assert.Same(vm.Proposals, speaking.Proposal);
        Assert.False(speaking.CanBreakDown);
        Assert.Null(email.Proposal);
        vm.Proposals!.AddCommand.Execute(null);
        Assert.Equal(5, vm.OpenItems.Single(r => r.Title == "Ôn Speaking").Subtasks.Count); // the checklist under the task
        vm.Proposals.CloseCommand.Execute(null);
        Assert.Null(speaking.Proposal);
        Assert.True(speaking.CanBreakDown);
    }

    // ---- Make tasks (a note) --------------------------------------------------------------------------------------

    private const string Note = "- Mua quà sinh nhật mẹ (thứ 7)\n- Đóng tiền điện tháng 11, 680k\n- Gọi Nam hỏi trả 200k\n\n- Tuần sau về quê";

    private const string NoteAnswer = """
        {"tasks": [{"title": "Mua quà sinh nhật mẹ", "due": "2026-11-07", "line": "- Mua quà sinh nhật mẹ (thứ 7)"},
                   {"title": "Đóng tiền điện tháng 11", "notes": "680.000đ", "due": "2026-11-04", "line": "Đóng tiền điện tháng 11, 680k"},
                   {"title": "Gọi Nam hỏi trả 200k"}]}
        """;

    [Fact]
    public void A_notes_lines_are_matched_to_tasks_skipped_ones_named_and_tags_come_and_go()
    {
        const string tag = " → Tracker";
        var lines = NoteTaskLines.Match(Note, [("Mua quà", "- Mua quà sinh nhật mẹ (thứ 7)"), ("Đóng tiền điện", "đóng tiền điện tháng 11, 680K"), ("Gọi Nam hỏi trả 200k", null), ("Đi chợ", null)], tag);
        Assert.Equal([0, 1, 2, -1], lines);
        Assert.Equal("Skipped “Tuần sau về quê”: not a task.", NoteTaskLines.Skipped(Note, lines, tag));
        Assert.Equal("Skipped “A”, “B”, “C” and 1 more: not tasks.", NoteTaskLines.Skipped("A\nB\n\nC\nD", [], tag));
        Assert.Equal("", NoteTaskLines.Skipped("1. Buy milk", [0], tag));

        var tagged = NoteTaskLines.AddTags(Note, [0, 2], tag);
        Assert.Equal("- Mua quà sinh nhật mẹ (thứ 7) → Tracker", NoteTaskLines.Split(tagged)[0]);
        Assert.Equal("- Gọi Nam hỏi trả 200k → Tracker", NoteTaskLines.Split(tagged)[2]);
        // A tagged line is never matched (nor skipped) again.
        Assert.Equal([-1], NoteTaskLines.Match(tagged, [("Gọi Nam hỏi trả 200k", null)], tag));
        Assert.DoesNotContain("Mua quà", NoteTaskLines.Skipped(tagged, [], tag));
        Assert.Equal(Note, NoteTaskLines.RemoveTags(tagged, ["- Mua quà sinh nhật mẹ (thứ 7) → Tracker", "- Gọi Nam hỏi trả 200k → Tracker"], tag));
    }

    [Fact]
    public void A_tagged_line_shows_its_task_done_and_open_again()
    {
        const string tag = " → Tracker";
        var text = "- Mua quà → Tracker\n- Đi chợ → Tracker\nGhi chú";
        NoteTaskLink[] links = [new("t1", "- Mua quà"), new("t2", "- Đi chợ"), new("gone", "Ghi chú")];
        var done = new Dictionary<string, bool?> { ["t1"] = true, ["t2"] = false, ["gone"] = null };

        var marked = NoteTaskLines.MarkDone(text, links, tag, id => done[id]);

        Assert.Equal("- Mua quà → Tracker ✓\n- Đi chợ → Tracker\nGhi chú", marked);
        Assert.True(NoteTaskLines.IsTagged("- Mua quà → Tracker ✓", tag));
        // A done line is not offered again, nor named as skipped.
        Assert.Equal([-1], NoteTaskLines.Match(marked, [("Mua quà", "- Mua quà")], tag));
        Assert.DoesNotContain("Mua quà", NoteTaskLines.Skipped(marked, [], tag));
        done["t1"] = false;
        Assert.Equal(text, NoteTaskLines.MarkDone(marked, links, tag, id => done[id]));
        // Undo of "Make tasks" takes the tag off a line marked done meanwhile too.
        Assert.Equal("- Mua quà\n- Đi chợ → Tracker\nGhi chú", NoteTaskLines.RemoveTags(marked, ["- Mua quà → Tracker"], tag));
    }

    [Fact]
    public async Task A_task_made_from_a_note_line_shows_done_in_the_note_and_Undo_forgets_it()
    {
        var home = _tracker.AddWorkspace("Nhà");
        var id = _notes.Add("Việc cuối tuần", Note);
        using var vm = NotesWith(new TrackerNoteTasks(_tracker, new ScriptedAiText(NoteAnswer), _time));
        vm.OpenNote(id);
        await vm.MakeTasksCommand.ExecuteAsync(null);
        vm.Tasks!.Choices[2].Keep = false;
        vm.Tasks.AddCommand.Execute(null);
        vm.Flush();

        var links = _notes.Get(id)!.TaskLinks;
        Assert.Equal(["- Mua quà sinh nhật mẹ (thứ 7)", "- Đóng tiền điện tháng 11, 680k"], links.Select(l => l.Line));
        var gift = _tracker.Items(home).Single(i => i.Value.Title == "Mua quà sinh nhật mẹ").Id;
        Assert.Equal(gift, links[0].TaskId);

        _tracker.Complete(gift);
        Assert.Equal("- Mua quà sinh nhật mẹ (thứ 7) → Tracker ✓", NoteTaskLines.Split(vm.EditBody)[0]);
        Assert.Equal("- Đóng tiền điện tháng 11, 680k → Tracker", NoteTaskLines.Split(vm.EditBody)[1]);

        _tracker.Reopen(gift);
        Assert.Equal("- Mua quà sinh nhật mẹ (thứ 7) → Tracker", NoteTaskLines.Split(vm.EditBody)[0]);

        // Done again, then the whole "Make tasks" taken back: the tasks, the tags and the links go.
        _tracker.Complete(gift);
        vm.Tasks.UndoCommand.Execute(null);
        Assert.Equal(Note, vm.EditBody);
        Assert.Empty(_tracker.Items(home));
        Assert.Empty(_notes.Get(id)!.TaskLinks);
    }

    [Fact]
    public void Opening_a_note_marks_the_lines_whose_tasks_were_done_meanwhile()
    {
        var list = _tracker.AddWorkspace("Nhà");
        var task = _tracker.AddItem(list, new TrackerItemDraft("Mua quà"));
        var id = _notes.Add("Việc", "- Mua quà → Tracker\n- Khác");
        _notes.AddTaskLinks(id, [new NoteTaskLink(task, "- Mua quà")]);
        _tracker.Complete(task);
        using var vm = NotesWith(new TrackerNoteTasks(_tracker, new ScriptedAiText(), _time));

        vm.OpenNote(id);
        vm.Flush();

        Assert.Equal("- Mua quà → Tracker ✓\n- Khác", _notes.Get(id)!.Body);
        Assert.Single(_notes.Get(id)!.TaskLinks);
    }

    private NotesViewModel NotesWith(TrackerNoteTasks tasks) =>
        new(_notes, _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), NullLogger<NotesViewModel>.Instance, taskMaker: tasks);

    [Fact]
    public async Task Make_tasks_adds_the_ticked_ones_to_the_one_list_picked_tags_their_lines_and_Undo_takes_all_back()
    {
        _tracker.AddWorkspace("Cá nhân");
        var home = _tracker.AddWorkspace("Nhà");
        var id = _notes.Add("Việc cuối tuần", Note);
        using var vm = NotesWith(new TrackerNoteTasks(_tracker, new ScriptedAiText(NoteAnswer), _time));
        vm.OpenNote(id);
        Assert.True(vm.CanMakeTasks);

        await vm.MakeTasksCommand.ExecuteAsync(null);

        var panel = vm.Tasks!;
        Assert.True(panel.IsProposing);
        Assert.Equal(["Cá nhân", "Nhà"], panel.Lists);
        Assert.Equal("Cá nhân", panel.SelectedList);
        Assert.Equal(3, panel.Choices.Count);
        Assert.Equal(TaskProposals.DayText(new DateOnly(2026, 11, 7)), panel.Choices[0].DueText);
        Assert.False(panel.Choices[2].HasDue);
        Assert.Equal("Skipped “Tuần sau về quê”: not a task.", panel.SkippedText);

        panel.SelectedList = "Nhà";
        panel.Choices[2].Keep = false;
        Assert.Equal("Add 2 tasks", panel.AddText);
        panel.AddCommand.Execute(null);

        Assert.True(panel.IsDone);
        Assert.Equal("2 tasks added to Nhà", panel.DoneText);
        Assert.Equal(["Mua quà sinh nhật mẹ", "Đóng tiền điện tháng 11"], _tracker.Items(home).Select(i => i.Value.Title).Order(StringComparer.Ordinal));
        Assert.Equal("680.000đ", _tracker.Items(home).Single(i => i.Value.Title == "Đóng tiền điện tháng 11").Value.Notes);
        var lines = NoteTaskLines.Split(vm.EditBody);
        Assert.EndsWith(" → Tracker", lines[0]);
        Assert.EndsWith(" → Tracker", lines[1]);
        Assert.Equal("- Gọi Nam hỏi trả 200k", lines[2]);
        vm.Flush();
        Assert.Contains("680k → Tracker", _notes.Get(id)!.Body);

        panel.UndoCommand.Execute(null);
        Assert.Empty(_tracker.Items(home));
        Assert.Equal(Note, vm.EditBody);
        Assert.True(panel.IsProposing); // back to the proposals, to pick again or cancel
    }

    [Fact]
    public async Task A_list_named_that_does_not_exist_is_made_and_Undo_removes_it_and_a_new_note_closes_the_panel()
    {
        var id = _notes.Add("Họp", "Gửi báo cáo thứ 6");
        var other = _notes.Add("Khác", "Mua sữa");
        using var vm = NotesWith(new TrackerNoteTasks(_tracker, new ScriptedAiText("""{"tasks": [{"title": "Gửi báo cáo", "due": "2026-11-06"}]}"""), _time));
        vm.OpenNote(id);

        await vm.MakeTasksCommand.ExecuteAsync(null);
        var panel = vm.Tasks!;
        Assert.Equal(["To-do"], panel.Lists); // no list yet: the default one is made on Add
        Assert.Equal("", panel.SkippedText);
        panel.AddCommand.Execute(null);
        Assert.Single(_tracker.Workspaces());
        panel.UndoCommand.Execute(null);
        Assert.Empty(_tracker.Workspaces());

        vm.OpenNote(other);
        Assert.False(panel.IsOpen);
    }

    [Fact]
    public void Make_tasks_is_offered_only_with_AI_and_Tracker_on_and_not_for_a_note_in_the_trash()
    {
        var id = _notes.Add("Họp", "Gửi báo cáo");
        using var none = new NotesViewModel(_notes, _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), NullLogger<NotesViewModel>.Instance);
        none.OpenNote(id);
        Assert.False(none.CanMakeTasks);

        using var noAi = NotesWith(new TrackerNoteTasks(_tracker, null, _time));
        noAi.OpenNote(id);
        Assert.False(noAi.CanMakeTasks);

        using var vm = NotesWith(new TrackerNoteTasks(_tracker, new ScriptedAiText(), _time));
        Assert.True(vm.OffersTasks);
        Assert.False(vm.CanMakeTasks); // no note open
        vm.OpenNote(id);
        Assert.True(vm.CanMakeTasks);
        _settings.Get<GeneralSettings>(GeneralSettings.StoreId).Update(s => s.EnabledModules[TrackerIds.ModuleId] = false);
        Assert.False(vm.CanMakeTasks);
    }

    /// <summary>Answers when the test says (in order, whenever the call comes), and says where it runs.</summary>
    private sealed class GatedAiText(string where) : IAiText
    {
        private readonly System.Threading.Channels.Channel<AiTextAnswer> _answers = System.Threading.Channels.Channel.CreateUnbounded<AiTextAnswer>();

        public string? Unavailable(string feature) => null;

        public string? Where(string feature) => where;

        public Task<AiTextAnswer> AskAsync(AiTextRequest request, CancellationToken ct) => _answers.Reader.ReadAsync(ct).AsTask();

        public void Answer(AiTextAnswer answer) => _answers.Writer.TryWrite(answer);
    }
}
