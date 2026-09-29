using Helm.Core.Sync;

namespace Helm.Modules.Notes;

/// <summary>
/// Notes on top of Helm Sync. Every write is local and immediate (the sync engine uploads it in the background). No
/// text is ever lost: two devices editing the same note keep both versions (the sync policy is KeepBoth, and the
/// editor's own save does the same when a newer version arrived while it was open), an edit brings a note back from
/// the trash, and only notes in the trash are ever deleted. Callable from any thread; <see cref="Changed"/> may be
/// raised on a background thread after a sync.
/// </summary>
public sealed class NotesStore
{
    public const string Collection = "notes.items";

    /// <summary>Notes stay this many days in the trash, then they are deleted for good.</summary>
    public const int TrashDays = 30;

    public const string ConflictSuffix = " (conflict copy)";

    private readonly ISyncedCollection<NoteItem> _notes;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public NotesStore(ISyncedCollection<NoteItem> notes, TimeProvider? time = null)
    {
        _notes = notes;
        _time = time ?? TimeProvider.System;
        _notes.Changed += (_, e) => Changed?.Invoke(this, e);
    }

    /// <summary>The collection options: KeepBoth, and bulk deletions of notes that are not in the trash are held.</summary>
    public static SyncedCollectionOptions<NoteItem> Options { get; } = new()
    {
        Name = Collection,
        ConflictPolicy = SyncConflictPolicy.KeepBoth,
        CreateConflictCopy = note => note with { Title = note.DisplayTitle + ConflictSuffix, Rev = NewRev() },
        GuardDeletions = true,
        IsExpendable = note => note.Trashed,
    };

    /// <summary>Raised after any local write or synced change (on the writing thread or a thread-pool thread).</summary>
    public event EventHandler<SyncedChangedEventArgs>? Changed;

    public DateTimeOffset Now => _time.GetUtcNow();

    public NoteItem? Get(string id) => _notes.Get(id);

    /// <summary>Notes that are not in the trash: pinned first, then in <paramref name="order"/>.</summary>
    public IReadOnlyList<SyncedItem<NoteItem>> Notes(NotesSortOrder order = NotesSortOrder.Edited)
    {
        var live = _notes.All().Where(n => !n.Value.Trashed);
        var pinnedFirst = live.OrderByDescending(n => n.Value.Pinned);
        var sorted = order switch
        {
            NotesSortOrder.Created => pinnedFirst.ThenByDescending(n => n.Value.CreatedAt),
            NotesSortOrder.Title => pinnedFirst.ThenBy(n => n.Value.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
            _ => pinnedFirst.ThenByDescending(n => n.Value.EditedAt),
        };
        return sorted.ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Notes in the trash, most recently trashed first.</summary>
    public IReadOnlyList<SyncedItem<NoteItem>> Trash() =>
        _notes.All().Where(n => n.Value.Trashed).OrderByDescending(n => n.Value.TrashedAt).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();

    /// <summary>Creates a note with the text and returns its id.</summary>
    /// <exception cref="ArgumentException">The text is empty or longer than <see cref="NoteItem.MaxLength"/>.</exception>
    public string Add(string title, string body, bool pinned = false)
    {
        (title, body) = Clean(title, body);
        if (title.Length == 0 && body.Trim().Length == 0) throw new ArgumentException("The note is empty.", nameof(body));
        var now = Now;
        return _notes.Add(new NoteItem
        {
            Title = title,
            Body = body,
            Pinned = pinned,
            CreatedAt = now,
            EditedAt = now,
            Rev = NewRev(),
        });
    }

    /// <summary>
    /// Saves the editor's text over the note. <paramref name="baseRev"/> is the revision the editor opened (or last
    /// saved): if the note has another one now, it was edited on another device meanwhile, so that version stays and
    /// the editor's text is saved as a new note next to it. Saving brings a note back from the trash.
    /// </summary>
    /// <exception cref="ArgumentException">The text is longer than <see cref="NoteItem.MaxLength"/>.</exception>
    public NoteSaveResult Save(string id, string title, string body, string baseRev)
    {
        (title, body) = Clean(title, body);
        lock (_gate)
        {
            var current = _notes.Get(id);
            if (current is null)
            {
                // Deleted for good on another device while it was open here: keep the text as a new note.
                if (title.Length == 0 && body.Trim().Length == 0) return new NoteSaveResult(NoteSaveOutcome.Unchanged, id, baseRev);
                var newId = Add(title, body);
                return new NoteSaveResult(NoteSaveOutcome.Recreated, newId, _notes.Get(newId)!.Rev);
            }
            if (current.Title == title && current.Body == body)
            {
                if (current.Trashed) _notes.Upsert(id, current with { Trashed = false, TrashedAt = null });
                return new NoteSaveResult(NoteSaveOutcome.Unchanged, id, current.Rev);
            }
            var now = Now;
            if (current.Rev != baseRev)
            {
                var copyId = _notes.Add(new NoteItem
                {
                    Title = NoteText.DisplayTitle(title, body) + ConflictSuffix,
                    Body = body,
                    CreatedAt = now,
                    EditedAt = now,
                    Rev = NewRev(),
                });
                return new NoteSaveResult(NoteSaveOutcome.SavedAsCopy, id, current.Rev, copyId);
            }
            var rev = NewRev();
            _notes.Upsert(id, current with { Title = title, Body = body, EditedAt = now, Rev = rev, Trashed = false, TrashedAt = null });
            return new NoteSaveResult(NoteSaveOutcome.Saved, id, rev);
        }
    }

    public bool SetPinned(string id, bool pinned) => Change(id, n => n with { Pinned = pinned });

    /// <summary>Moves a note to the trash (restorable for <see cref="TrashDays"/> days).</summary>
    public bool MoveToTrash(string id) => Change(id, n => n.Trashed ? n : n with { Trashed = true, TrashedAt = Now, Pinned = false });

    public bool Restore(string id) => Change(id, n => n with { Trashed = false, TrashedAt = null });

    /// <summary>Deletes a note in the trash for good. Notes outside the trash are never deleted.</summary>
    public bool DeleteForever(string id)
    {
        lock (_gate)
        {
            return _notes.Get(id) is { Trashed: true } && _notes.Delete(id);
        }
    }

    /// <returns>How many notes were deleted.</returns>
    public int EmptyTrash()
    {
        lock (_gate)
        {
            return Trash().Count(n => _notes.Delete(n.Id));
        }
    }

    /// <summary>Deletes notes that have been in the trash longer than <see cref="TrashDays"/> days.</summary>
    /// <returns>How many notes were deleted.</returns>
    public int PurgeTrash()
    {
        var cutoff = Now - TimeSpan.FromDays(TrashDays);
        lock (_gate)
        {
            return Trash().Where(n => n.Value.TrashedAt is { } at && at < cutoff).Count(n => _notes.Delete(n.Id));
        }
    }

    private bool Change(string id, Func<NoteItem, NoteItem> change)
    {
        lock (_gate)
        {
            if (_notes.Get(id) is not { } current) return false;
            var updated = change(current);
            if (updated != current) _notes.Upsert(id, updated);
            return true;
        }
    }

    private static (string Title, string Body) Clean(string title, string body)
    {
        title = NoteText.Normalize(title).Replace('\n', ' ').Trim();
        body = NoteText.Normalize(body).TrimEnd();
        if (title.Length + body.Length > NoteItem.MaxLength)
            throw new ArgumentException($"A note can hold up to {NoteItem.MaxLength:N0} characters. Split it into two notes.", nameof(body));
        return (title, body);
    }

    private static string NewRev() => Guid.NewGuid().ToString("N");
}
