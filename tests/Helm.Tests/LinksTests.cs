using Helm.Core.Links;
using Helm.Modules.Notes;
using Helm.Modules.Tracker;

namespace Helm.Tests;

/// <summary>Links between tools: notes on tasks and on people in the debt book, both ways.</summary>
public sealed class LinksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(T0);
    private readonly MemorySynced<HelmLink> _links = new();
    private readonly NotesStore _notes;
    private readonly TrackerStore _tracker;
    private readonly List<string> _opened = [];
    private readonly LinkHub _hub;

    public LinksTests()
    {
        _notes = new NotesStore(new MemorySynced<NoteItem>(), _time);
        _tracker = new TrackerStore(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>(), _time);
        _hub = new LinkHub(_links, () =>
        [
            new NoteLinkProvider(_notes, id => _opened.Add("note " + id)),
            new TaskLinkProvider(_tracker, (ws, item) => _opened.Add($"task {ws} {item}")),
            new PersonLinkProvider(_tracker, (book, key) => _opened.Add($"person {book} {key}")),
        ], _time);
    }

    [Fact]
    public void The_same_pair_is_one_record_whichever_end_links_first()
    {
        var note = new LinkRef(LinkKinds.Note, "n1");
        var task = new LinkRef(LinkKinds.Task, "t1");
        Assert.Equal(LinkHub.IdOf(note, task), LinkHub.IdOf(task, note));
        Assert.StartsWith("l", LinkHub.IdOf(note, task));

        Assert.True(_hub.Link(note, task));
        Assert.False(_hub.Link(task, note));
        Assert.Single(_links.All());
        Assert.Equal([task], _hub.LinksOf(note));
        Assert.Equal([note], _hub.LinksOf(task));

        Assert.True(_hub.Unlink(task, note));
        Assert.Empty(_hub.LinksOf(note));
        Assert.Throws<ArgumentException>(() => _hub.Link(note, note));
    }

    [Fact]
    public void A_task_shows_its_notes_and_a_note_its_task_and_person()
    {
        var list = _tracker.AddWorkspace("Work", WorkspaceKind.Tasks);
        var book = _tracker.AddWorkspace("Debts", WorkspaceKind.Debts);
        var task = _tracker.AddItem(list, new TrackerItemDraft("Ship v1"));
        _tracker.AddDebt(book, "Nam", 200_000, DebtEntryKind.OwesMe, "lunch");
        var note = _notes.Add("Release notes", "what changed");

        var taskLinks = new LinksViewModel(_hub, new LinkRef(LinkKinds.Task, task), [LinkKinds.Note], () => "Ship v1");
        taskLinks.StartPickingCommand.Execute(null);
        var found = Assert.Single(taskLinks.Results);
        Assert.Equal("Release notes", found.Title);
        taskLinks.PickCommand.Execute(found);
        Assert.False(taskLinks.IsPicking);
        Assert.Equal("Release notes", Assert.Single(taskLinks.Items).Title);

        var noteLinks = new LinksViewModel(_hub, new LinkRef(LinkKinds.Note, note), [LinkKinds.Task, LinkKinds.Person], () => "Release notes");
        Assert.Equal("Ship v1", Assert.Single(noteLinks.Items).Title);
        Assert.Equal("Link a task or person…", noteLinks.PickText);
        Assert.False(noteLinks.CanCreate);
        noteLinks.StartPickingCommand.Execute(null);
        noteLinks.Query = "nam";
        var person = Assert.Single(noteLinks.Results);
        Assert.Equal((LinkKinds.Person, "nam"), (person.Ref.Kind, person.Ref.Id));
        Assert.StartsWith("Debt · ", person.Subtitle);
        noteLinks.PickCommand.Execute(person);
        Assert.Equal(["Ship v1", "Nam"], noteLinks.Items.Select(i => i.Title));

        noteLinks.Items[1].OpenCommand.Execute(null);
        noteLinks.Items[0].OpenCommand.Execute(null);
        Assert.Equal([$"person {book} nam", $"task {list} {task}"], _opened);
    }

    [Fact]
    public void New_note_from_a_task_is_titled_after_it_linked_and_opened()
    {
        var list = _tracker.AddWorkspace("Work", WorkspaceKind.Tasks);
        var task = _tracker.AddItem(list, new TrackerItemDraft("Plan the trip"));
        var links = new LinksViewModel(_hub, new LinkRef(LinkKinds.Task, task), [LinkKinds.Note], () => "Plan the trip");
        Assert.True(links.CanCreate);
        Assert.Equal("New note", links.CreateText);
        Assert.Equal("Link a note…", links.PickText);

        links.StartPickingCommand.Execute(null);
        links.CreateCommand.Execute(null);

        Assert.False(links.IsPicking);
        var (id, note, _) = Assert.Single(_notes.Notes());
        Assert.Equal("Plan the trip", note.Title);
        Assert.Equal("Plan the trip", Assert.Single(links.Items).Title);
        Assert.Equal(["note " + id], _opened);
    }

    [Fact]
    public void A_note_in_the_trash_is_hidden_from_its_links_until_restored()
    {
        var list = _tracker.AddWorkspace("Work", WorkspaceKind.Tasks);
        var task = new LinkRef(LinkKinds.Task, _tracker.AddItem(list, new TrackerItemDraft("Call the bank")));
        var note = _notes.Add("Bank", "account 123");
        _hub.Link(task, new LinkRef(LinkKinds.Note, note));

        _notes.MoveToTrash(note);
        Assert.Empty(_hub.TargetsOf(task));
        Assert.Single(_hub.LinksOf(task));
        _notes.Restore(note);
        Assert.Equal("Bank", Assert.Single(_hub.TargetsOf(task)).Title);
    }

    [Fact]
    public void A_repeating_task_is_linked_as_its_series_so_every_day_keeps_the_note()
    {
        var list = _tracker.AddWorkspace("Habits", WorkspaceKind.Tasks);
        var today = _tracker.AddItem(list, new TrackerItemDraft("Stretch", RepeatDaily: true));
        var series = _tracker.GetItem(today)!.SeriesId!;
        var note = _notes.Add("Stretch routine", "5 minutes");
        _hub.Link(new LinkRef(LinkKinds.Task, TaskLinkProvider.LinkId(today, _tracker.GetItem(today)!)), new LinkRef(LinkKinds.Note, note));

        _time.Advance(TimeSpan.FromDays(1));
        _tracker.EnsureRepeats();
        var tomorrow = TrackerStore.OccurrenceId(series, DateOnly.FromDateTime(_time.Now.LocalDateTime));
        Assert.NotNull(_tracker.GetItem(tomorrow));
        var links = new LinksViewModel(_hub, new LinkRef(LinkKinds.Task, TaskLinkProvider.LinkId(tomorrow, _tracker.GetItem(tomorrow)!)),
            [LinkKinds.Note], () => "Stretch");
        Assert.Equal("Stretch routine", Assert.Single(links.Items).Title);

        // From the note, the series opens its latest day.
        var fromNote = Assert.Single(_hub.TargetsOf(new LinkRef(LinkKinds.Note, note)));
        _hub.Provider(LinkKinds.Task)!.Open(fromNote.Ref.Id);
        Assert.Equal([$"task {list} {tomorrow}"], _opened);
    }

    [Fact]
    public void The_hub_tells_when_links_or_their_targets_change()
    {
        var changes = 0;
        _hub.Changed += (_, _) => changes++;
        Assert.NotNull(_hub.Provider(LinkKinds.Note)); // providers subscribe on first use
        _notes.Add("Anything", "");
        _hub.Link(new LinkRef(LinkKinds.Note, "a"), new LinkRef(LinkKinds.Task, "b"));
        Assert.True(changes >= 2);
    }
}
