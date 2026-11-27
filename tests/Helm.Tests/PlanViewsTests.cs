using Helm.Core.Settings;
using Helm.Modules.Notes;
using Helm.Modules.Tracker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// A note's lines that became tasks (Notes' "Make tasks" before 0.96) keep their "→ Tracker" tag showing whether each
/// task is done, through Tracker's task bridge.
/// </summary>
public sealed class PlanViewsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(T0);
    private readonly TrackerStore _tracker;
    private readonly MemorySynced<NoteItem> _noteItems = new();
    private readonly NotesStore _notes;
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public PlanViewsTests()
    {
        _tracker = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
        _notes = new NotesStore(_noteItems, _time);
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
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
        done["t1"] = false;
        Assert.Equal(text, NoteTaskLines.MarkDone(marked, links, tag, id => done[id]));
        Assert.Equal(tag, NoteTaskLines.Tag("Tracker"));
    }

    [Fact]
    public void Opening_a_note_marks_the_lines_whose_tasks_were_done_meanwhile_and_the_open_one_follows_its_tasks()
    {
        var list = _tracker.AddWorkspace("Nhà");
        var task = _tracker.AddItem(list, new TrackerItemDraft("Mua quà"));
        var id = _notes.Add("Việc", "- Mua quà → Tracker\n- Khác");
        // A link "Make tasks" kept on the note before 0.96 (synced like the rest of the note).
        _noteItems.Upsert(id, _noteItems.Get(id)! with { TaskLinks = [new NoteTaskLink(task, "- Mua quà")] });
        _tracker.Complete(task);
        using var vm = new NotesViewModel(_notes, _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), NullLogger<NotesViewModel>.Instance,
            tasks: new TrackerTaskBridge(_tracker));

        vm.OpenNote(id);
        vm.Flush();
        Assert.Equal("- Mua quà → Tracker ✓\n- Khác", _notes.Get(id)!.Body);

        _tracker.Reopen(task);
        Assert.Equal("- Mua quà → Tracker\n- Khác", vm.EditBody);
        Assert.Single(_notes.Get(id)!.TaskLinks);
    }
}
