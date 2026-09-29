using Helm.Core.Settings;
using Helm.Modules.Notes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public sealed class NotesTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(T0);
    private readonly MemorySynced<NoteItem> _notes = new();
    private readonly NotesStore _store;
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public NotesTests()
    {
        _store = new NotesStore(_notes, _time);
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    // ---- Store ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Notes_are_listed_pinned_first_then_most_recently_edited()
    {
        var a = _store.Add("A", "first");
        _time.Advance(TimeSpan.FromMinutes(1));
        var b = _store.Add("B", "second");
        _time.Advance(TimeSpan.FromMinutes(1));
        var c = _store.Add("", "Third note\nwith a body");
        _store.SetPinned(a, true);

        Assert.Equal([a, c, b], _store.Notes().Select(n => n.Id));
        Assert.Equal("Third note", _store.Get(c)!.DisplayTitle);
        Assert.Equal([a, b, c], _store.Notes(NotesSortOrder.Title).Select(n => n.Id)); // A, B, Third
    }

    [Fact]
    public void A_save_over_the_revision_the_editor_opened_replaces_the_text()
    {
        var id = _store.Add("Plan", "draft");
        var rev = _store.Get(id)!.Rev;
        _time.Advance(TimeSpan.FromMinutes(5));

        var result = _store.Save(id, "Plan", "final", rev);

        Assert.Equal(NoteSaveOutcome.Saved, result.Outcome);
        var note = _store.Get(id)!;
        Assert.Equal("final", note.Body);
        Assert.Equal(result.Rev, note.Rev);
        Assert.NotEqual(rev, note.Rev);
        Assert.Equal(T0.AddMinutes(5), note.EditedAt);
        Assert.Equal(T0, note.CreatedAt);
        Assert.Single(_store.Notes());
    }

    [Fact]
    public void A_save_over_a_version_from_another_device_keeps_both_texts()
    {
        var id = _store.Add("Plan", "draft");
        var rev = _store.Get(id)!.Rev;
        // Another device edits the note while this editor still has "rev".
        _notes.SetRemote(id, _store.Get(id)! with { Body = "their text", Rev = "remote-rev" });

        var result = _store.Save(id, "Plan", "my text", rev);

        Assert.Equal(NoteSaveOutcome.SavedAsCopy, result.Outcome);
        Assert.Equal("their text", _store.Get(id)!.Body);
        var copy = _store.Get(result.CopyId!)!;
        Assert.Equal("my text", copy.Body);
        Assert.Equal("Plan" + NotesStore.ConflictSuffix, copy.Title);
        Assert.Equal(2, _store.Notes().Count);
    }

    [Fact]
    public void Text_saved_after_the_note_was_deleted_elsewhere_becomes_a_new_note()
    {
        var id = _store.Add("", "keep me");
        var rev = _store.Get(id)!.Rev;
        _notes.SetRemote(id, null);

        var result = _store.Save(id, "", "keep me, edited", rev);

        Assert.Equal(NoteSaveOutcome.Recreated, result.Outcome);
        Assert.NotEqual(id, result.Id);
        Assert.Equal("keep me, edited", _store.Get(result.Id)!.Body);
    }

    [Fact]
    public void Unchanged_text_is_not_written_again()
    {
        var id = _store.Add("T", "same");
        var before = _store.Get(id)!;
        var writes = 0;
        _notes.Changed += (_, _) => writes++;

        var result = _store.Save(id, " T ", "same\r\n", before.Rev);

        Assert.Equal(NoteSaveOutcome.Unchanged, result.Outcome);
        Assert.Equal(0, writes);
        Assert.Same(before, _store.Get(id));
    }

    [Fact]
    public void Trash_keeps_notes_until_they_are_old_and_editing_brings_one_back()
    {
        var keep = _store.Add("", "restore me");
        var old = _store.Add("", "forget me");
        _store.SetPinned(old, true);
        _store.MoveToTrash(old);
        Assert.False(_store.Get(old)!.Pinned); // the trash has no pins
        _time.Advance(TimeSpan.FromDays(10));
        _store.MoveToTrash(keep);

        Assert.Empty(_store.Notes());
        Assert.Equal([keep, old], _store.Trash().Select(n => n.Id));
        Assert.False(_store.DeleteForever(_store.Add("", "live"))); // only notes in the trash are ever deleted

        _time.Advance(TimeSpan.FromDays(NotesStore.TrashDays - 5));
        Assert.Equal(1, _store.PurgeTrash()); // "old" has been in the trash for 35 days
        Assert.Null(_store.Get(old));

        var saved = _store.Save(keep, "", "restore me, please", _store.Get(keep)!.Rev);
        Assert.Equal(NoteSaveOutcome.Saved, saved.Outcome);
        Assert.False(_store.Get(keep)!.Trashed);
    }

    [Fact]
    public void Notes_have_a_size_limit_that_fits_a_synced_record()
    {
        var huge = new string('ệ', NoteItem.MaxLength + 1);
        Assert.Throws<ArgumentException>(() => _store.Add("", huge));
        Assert.Throws<ArgumentException>(() => _store.Add(" ", "  \n "));
        // The limit leaves room for JSON escaping (\uXXXX, six bytes a letter) inside 1 MiB.
        Assert.True(NoteItem.MaxLength * 6 < 1024 * 1024);
    }

    [Fact]
    public void The_sync_conflict_copy_is_named_after_the_note()
    {
        var copy = NotesStore.Options.CreateConflictCopy!(new NoteItem { Title = "", Body = "Shopping\nmilk", Rev = "x" });
        Assert.Equal("Shopping" + NotesStore.ConflictSuffix, copy.Title);
        Assert.NotEqual("x", copy.Rev);
        Assert.True(NotesStore.Options.IsExpendable!(new NoteItem { Trashed = true }));
        Assert.False(NotesStore.Options.IsExpendable!(new NoteItem()));
    }

    [Fact]
    public void Text_helpers_title_preview_and_file_names()
    {
        Assert.Equal(NoteText.Untitled, NoteText.DisplayTitle("  ", " \n "));
        Assert.Equal("line two line three", NoteText.Preview("", "line one\n\nline two\nline three"));
        Assert.Equal("line one line two", NoteText.Preview("Title", "line one\nline two"));
        Assert.Equal("a_b_c", NoteText.FileName("a/b:c"));
        Assert.Equal(NoteText.Untitled, NoteText.FileName("..."));
    }

    // ---- View model ----------------------------------------------------------------------------------------------

    private NotesViewModel NewViewModel(MemoryClipboard? clipboard = null) =>
        new(_store, _settings, new InlineUi(), new AcceptDialogs(), clipboard ?? new MemoryClipboard(), NullLogger<NotesViewModel>.Instance);

    [Fact]
    public void A_new_note_is_created_only_once_something_is_typed()
    {
        using var vm = NewViewModel();
        vm.NewNoteCommand.Execute(null);
        Assert.True(vm.IsEditorOpen);
        vm.Flush();
        Assert.Empty(_store.Notes()); // nothing typed: nothing saved

        vm.EditBody = "Buy milk";
        vm.Flush();
        var note = Assert.Single(_store.Notes());
        Assert.Equal("Buy milk", note.Value.Body);

        vm.EditBody = "Buy milk and eggs";
        vm.Flush();
        Assert.Single(_store.Notes()); // the same note, not a second one
        Assert.Equal("Buy milk and eggs", _store.Get(note.Id)!.Body);
        Assert.Same(vm.Notes[0], vm.SelectedNote);
    }

    [Fact]
    public void A_synced_change_to_the_open_note_shows_up_when_nothing_is_being_typed()
    {
        var id = _store.Add("Plan", "v1");
        using var vm = NewViewModel();
        vm.OpenNote(id);
        Assert.Equal("v1", vm.EditBody);

        _notes.SetRemote(id, _store.Get(id)! with { Body = "v2 from the phone", Rev = "phone" });

        Assert.Equal("v2 from the phone", vm.EditBody);
        vm.EditBody = "v3";
        vm.Flush();
        Assert.Equal("v3", _store.Get(id)!.Body); // saved over the phone's version, which it had shown
        Assert.Single(_store.Notes());
    }

    [Fact]
    public void Typing_over_a_synced_change_keeps_both_and_carries_on_in_the_copy()
    {
        var id = _store.Add("Plan", "v1");
        using var vm = NewViewModel();
        vm.OpenNote(id);
        vm.EditBody = "typed here";
        // Arrives before the autosave: the editor keeps what is typed.
        _notes.SetRemote(id, _store.Get(id)! with { Body = "typed on the phone", Rev = "phone" });
        Assert.Equal("typed here", vm.EditBody);

        vm.Flush();

        Assert.Equal("typed on the phone", _store.Get(id)!.Body);
        var copy = _store.Notes().Single(n => n.Id != id);
        Assert.Equal("typed here", copy.Value.Body);
        Assert.True(vm.HasMessage);
        Assert.Equal(copy.Id, vm.SelectedNote?.Id);

        vm.EditBody = "typed here, more";
        vm.Flush();
        Assert.Equal("typed here, more", _store.Get(copy.Id)!.Body);
        Assert.Equal("typed on the phone", _store.Get(id)!.Body);
    }

    [Fact]
    public void Search_ignores_accents_and_looks_in_the_text()
    {
        _store.Add("Ghi chú họp", "");
        _store.Add("Shopping", "sữa, trứng");
        _store.Add("Other", "");
        using var vm = NewViewModel();

        vm.Search = "ghi chu";
        Assert.Equal(["Ghi chú họp"], vm.Notes.Select(n => n.Title));
        vm.Search = "trung";
        Assert.Equal(["Shopping"], vm.Notes.Select(n => n.Title));
        vm.Search = "nothing like it";
        Assert.True(vm.IsListEmpty);
        Assert.Contains("nothing like it", vm.EmptyListText);
    }

    [Fact]
    public void Trash_and_restore_from_the_editor()
    {
        var id = _store.Add("Old", "text");
        using var vm = NewViewModel();
        vm.OpenNote(id);

        vm.TrashCommand.Execute(null);
        Assert.False(vm.IsEditorOpen);
        Assert.Equal(1, vm.TrashCount);
        Assert.Empty(vm.Notes);

        vm.ShowTrashCommand.Execute(null);
        vm.SelectedNote = Assert.Single(vm.Notes);
        Assert.True(vm.EditTrashed);
        vm.RestoreCommand.Execute(null);
        Assert.False(vm.ShowingTrash);
        Assert.False(_store.Get(id)!.Trashed);
        Assert.Equal(id, vm.SelectedNote?.Id);
    }

    [Fact]
    public void Copy_and_export_as_markdown()
    {
        var clipboard = new MemoryClipboard();
        _store.Add("Plan", "step 1");
        _store.Add("Plan", "other plan");
        _store.Add("", "no title");
        using var vm = NewViewModel(clipboard);
        vm.OpenNote(_store.Notes().First(n => n.Value.Body == "step 1").Id);
        vm.CopyNoteCommand.Execute(null);
        Assert.Equal("Plan\n\nstep 1", clipboard.Text);

        using var export = new TempDir();
        Assert.Equal(3, vm.ExportMarkdown(export.Path));
        var files = Directory.GetFiles(export.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Plan (2).md", "Plan.md", "no title.md"], files);
        Assert.StartsWith("# Plan\n\n", File.ReadAllText(Path.Combine(export.Path, "Plan.md")));
    }

    [Fact]
    public void Capture_target_uses_the_first_line_as_title_when_there_are_several()
    {
        var target = new NoteCaptureTarget(_store);
        Assert.False(target.Preview("   ").CanSave);
        Assert.True(target.Capture("just one line").Saved);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(target.Capture("Title line\nbody one\nbody two").Saved);

        var notes = _store.Notes(NotesSortOrder.Created).Select(n => n.Value).ToList();
        Assert.Equal(("Title line", "body one\nbody two"), (notes[0].Title, notes[0].Body));
        Assert.Equal(("", "just one line"), (notes[1].Title, notes[1].Body));
    }
}
