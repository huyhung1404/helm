using Helm.Core.Sync;

namespace Helm.Modules.WatchLater;

/// <summary>What happened when a link was saved.</summary>
/// <param name="Existed">The video was in the list already: it moved to the top (and back to "to watch").</param>
public sealed record WatchAddResult(string Id, bool Existed);

/// <summary>
/// Saved videos on top of Helm Sync. Every write is local and immediate (the sync engine uploads it in the background).
/// One record per video: saving a link that is already there (by <see cref="WatchItem.Key"/>) moves it to the top.
/// Callable from any thread; <see cref="Changed"/> may be raised on a background thread after a sync.
/// </summary>
public sealed class WatchLaterStore
{
    public const string Collection = "watch.items";

    private readonly ISyncedCollection<WatchItem> _items;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public WatchLaterStore(ISyncedCollection<WatchItem> items, TimeProvider? time = null)
    {
        _items = items;
        _time = time ?? TimeProvider.System;
        _items.Changed += (_, e) => Changed?.Invoke(this, e);
    }

    /// <summary>Last writer wins: a record is small and edits are single fields (watched, the note, a download).</summary>
    public static SyncedCollectionOptions<WatchItem> Options { get; } = new()
    {
        Name = Collection,
        ConflictPolicy = SyncConflictPolicy.LastWriterWins,
        GuardDeletions = true,
        IsExpendable = item => item.Watched,
    };

    /// <summary>Raised after any local write or synced change (on the writing thread or a thread-pool thread).</summary>
    public event EventHandler<SyncedChangedEventArgs>? Changed;

    public DateTimeOffset Now => _time.GetUtcNow();

    public WatchItem? Get(string id) => _items.Get(id);

    public IReadOnlyList<SyncedItem<WatchItem>> All() => _items.All();

    /// <summary>The record for a video, if it is saved.</summary>
    public SyncedItem<WatchItem>? Find(string key) => _items.All().FirstOrDefault(i => i.Value.Key == key);

    /// <summary>The videos a filter shows: newest first, or the most recently watched first for <see cref="WatchFilter.Watched"/>.</summary>
    public IReadOnlyList<SyncedItem<WatchItem>> Items(WatchFilter filter, WatchSource? source = null)
    {
        var items = _items.All().Where(i => source is null || i.Value.Source == source);
        items = filter switch
        {
            WatchFilter.Videos => items.Where(i => !i.Value.Watched && i.Value.Kind == WatchKind.Video),
            WatchFilter.Shorts => items.Where(i => !i.Value.Watched && i.Value.Kind == WatchKind.Short),
            WatchFilter.Watched => items.Where(i => i.Value.Watched),
            _ => items.Where(i => !i.Value.Watched),
        };
        var sorted = filter == WatchFilter.Watched
            ? items.OrderByDescending(i => i.Value.WatchedAt ?? i.Value.AddedAt)
            : items.OrderByDescending(i => i.Value.AddedAt);
        return sorted.ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>How many videos each filter shows.</summary>
    public (int ToWatch, int Videos, int Shorts, int Watched) Counts(WatchSource? source = null)
    {
        var items = _items.All().Where(i => source is null || i.Value.Source == source).Select(i => i.Value).ToList();
        return (items.Count(i => !i.Watched), items.Count(i => !i.Watched && i.Kind == WatchKind.Video),
            items.Count(i => !i.Watched && i.Kind == WatchKind.Short), items.Count(i => i.Watched));
    }

    /// <summary>
    /// Saves a video. Already saved: it moves to the top, is "to watch" again, and a new note is added under the old one.
    /// </summary>
    /// <exception cref="ArgumentException">The note is too long.</exception>
    public WatchAddResult Add(VideoLink link, string note = "")
    {
        note = CleanNote(note);
        lock (_gate)
        {
            var now = Now;
            if (Find(link.Key) is { } existing)
            {
                var old = existing.Value;
                var merged = note.Length == 0 || old.Note.Contains(note, StringComparison.Ordinal) ? old.Note
                    : old.Note.Length == 0 ? note : CleanNote(old.Note + "\n" + note);
                _items.Upsert(existing.Id, old with { AddedAt = now, Watched = false, WatchedAt = null, Note = merged });
                return new WatchAddResult(existing.Id, Existed: true);
            }
            var id = _items.Add(new WatchItem
            {
                Key = link.Key,
                Url = link.Url,
                OriginalUrl = link.OriginalUrl,
                Source = link.Source,
                Kind = link.Kind,
                ExternalId = link.ExternalId,
                Note = note,
                AddedAt = now,
                // A plain link to another site has nothing to look up; YouTube's thumbnail is known from the id.
                MetadataDone = link.Source == WatchSource.Other,
                ThumbnailUrl = link.Source == WatchSource.YouTube && link.ExternalId is { } yt ? YouTubeThumbnail(yt) : null,
            });
            return new WatchAddResult(id, Existed: false);
        }
    }

    public static string YouTubeThumbnail(string id) => $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";

    public bool SetWatched(string id, bool watched) =>
        Change(id, i => i.Watched == watched ? i : i with { Watched = watched, WatchedAt = watched ? Now : null });

    /// <exception cref="ArgumentException">The note is too long.</exception>
    public bool SetNote(string id, string note)
    {
        note = CleanNote(note);
        return Change(id, i => i with { Note = note });
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            return _items.Delete(id);
        }
    }

    /// <returns>How many watched videos were deleted.</returns>
    public int DeleteWatched()
    {
        lock (_gate)
        {
            return _items.All().Where(i => i.Value.Watched).Count(i => _items.Delete(i.Id));
        }
    }

    /// <summary>Fills in what was looked up; fields already known are only replaced by new non-empty values.</summary>
    public bool ApplyMetadata(string id, VideoMetadata metadata, bool done) => Change(id, i => i with
    {
        Title = Pick(metadata.Title, i.Title),
        Channel = Pick(metadata.Channel, i.Channel),
        ThumbnailUrl = Pick(metadata.ThumbnailUrl, i.ThumbnailUrl),
        DurationSeconds = metadata.DurationSeconds is > 0 ? metadata.DurationSeconds : i.DurationSeconds,
        MetadataDone = i.MetadataDone || done,
    });

    /// <summary>
    /// A short link (fb.watch, facebook.com/share/…) turned out to be <paramref name="resolved"/>. When that video is
    /// already saved the short-link record is merged into it (its note kept) and removed.
    /// </summary>
    /// <returns>The id of the record that now holds the video.</returns>
    public string Resolve(string id, VideoLink resolved)
    {
        lock (_gate)
        {
            if (_items.Get(id) is not { } current) return id;
            if (Find(resolved.Key) is { } other && other.Id != id)
            {
                var note = other.Value.Note.Length == 0 ? current.Note
                    : current.Note.Length == 0 || other.Value.Note.Contains(current.Note, StringComparison.Ordinal) ? other.Value.Note
                    : CleanNote(other.Value.Note + "\n" + current.Note);
                var newer = current.AddedAt > other.Value.AddedAt ? current.AddedAt : other.Value.AddedAt;
                _items.Upsert(other.Id, other.Value with { Note = note, AddedAt = newer, Watched = other.Value.Watched && current.Watched });
                _items.Delete(id);
                return other.Id;
            }
            _items.Upsert(id, current with
            {
                Key = resolved.Key,
                Url = resolved.Url,
                Kind = resolved.Kind,
                ExternalId = resolved.ExternalId,
            });
            return id;
        }
    }

    /// <summary>A phone asks a PC to download the video in <paramref name="quality"/>; null cancels the request.</summary>
    public bool RequestDownload(string id, string? quality) => Change(id, i => i with { DownloadRequest = quality });

    /// <summary>A PC downloaded the video for a request.</summary>
    public bool MarkDownloaded(string id, string device) =>
        Change(id, i => i with { DownloadRequest = null, DownloadedOn = device, DownloadedAt = Now });

    private bool Change(string id, Func<WatchItem, WatchItem> change)
    {
        lock (_gate)
        {
            if (_items.Get(id) is not { } current) return false;
            var updated = change(current);
            if (updated != current) _items.Upsert(id, updated);
            return true;
        }
    }

    private static string? Pick(string? found, string? known) => string.IsNullOrWhiteSpace(found) ? known : found.Trim();

    private static string CleanNote(string note)
    {
        note = note.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (note.Length > WatchItem.MaxNoteLength)
            throw new ArgumentException($"A note can hold up to {WatchItem.MaxNoteLength:N0} characters.", nameof(note));
        return note;
    }
}
