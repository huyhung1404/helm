using System.Text;
using Helm.Core.Sync;

namespace Helm.Modules.Scratch;

/// <summary>
/// Scratch on top of Helm Sync. A record per thing: text lives in the record, a file is encrypted into a blob at
/// once (the plaintext never touches Helm's folders) and uploaded by the sync engine before the record. Deleting moves
/// to the trash; only deleting from the trash removes a thing for good, on every device. Callable from any thread;
/// <see cref="Changed"/> may be raised on a background thread after a sync or an upload.
/// </summary>
public sealed class ScratchStore
{
    public const string Collection = "scratch.items";

    private readonly ISyncedCollection<ScratchItem> _items;
    private readonly BlobStore _blobs;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public ScratchStore(ISyncedCollection<ScratchItem> items, BlobStore blobs, TimeProvider? time = null)
    {
        _items = items;
        _blobs = blobs;
        _time = time ?? TimeProvider.System;
        _items.Changed += (_, e) => Changed?.Invoke(this, e);
        // An upload changes what a row says ("Waiting to upload" → nothing).
        _blobs.Uploaded += (_, id) => Changed?.Invoke(this, new SyncedChangedEventArgs([id], SyncChangeOrigin.Local));
    }

    /// <summary>
    /// Last writer wins (edits are single flags such as the trash), a pull that deletes many things outside the trash is
    /// held for the user, and the engine knows which blob each record uses.
    /// </summary>
    public static SyncedCollectionOptions<ScratchItem> Options { get; } = new()
    {
        Name = Collection,
        ConflictPolicy = SyncConflictPolicy.LastWriterWins,
        GuardDeletions = true,
        IsExpendable = item => item.Trashed,
        BlobReferences = item => item.Blob is { } blob ? [blob.Id] : [],
    };

    /// <summary>Raised after any local write, synced change or finished upload.</summary>
    public event EventHandler<SyncedChangedEventArgs>? Changed;

    public DateTimeOffset Now => _time.GetUtcNow();

    public ScratchItem? Get(string id) => _items.Get(id);

    /// <summary>What a filter shows: newest first, or the most recently deleted first in the trash.</summary>
    public IReadOnlyList<SyncedItem<ScratchItem>> Items(ScratchFilter filter)
    {
        var all = _items.All();
        if (filter == ScratchFilter.Trash)
            return all.Where(i => i.Value.Trashed).OrderByDescending(i => i.Value.TrashedAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        return all.Where(i => !i.Value.Trashed && Matches(i.Value, filter))
            .OrderByDescending(i => i.Value.Position).ThenByDescending(i => i.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>How many things each filter shows.</summary>
    public IReadOnlyDictionary<ScratchFilter, int> Counts()
    {
        var all = _items.All().Select(i => i.Value).ToList();
        var live = all.Where(i => !i.Trashed).ToList();
        return new Dictionary<ScratchFilter, int>
        {
            [ScratchFilter.All] = live.Count,
            [ScratchFilter.Media] = live.Count(i => Matches(i, ScratchFilter.Media)),
            [ScratchFilter.Files] = live.Count(i => Matches(i, ScratchFilter.Files)),
            [ScratchFilter.Text] = live.Count(i => Matches(i, ScratchFilter.Text)),
            [ScratchFilter.Trash] = all.Count - live.Count,
        };
    }

    public static bool Matches(ScratchItem item, ScratchFilter filter) => filter switch
    {
        ScratchFilter.Media => item.IsMedia,
        ScratchFilter.Files => item.Kind == ScratchKind.File && !item.IsMedia,
        ScratchFilter.Text => item.Kind == ScratchKind.Text,
        ScratchFilter.Trash => item.Trashed,
        _ => true,
    };

    /// <summary>
    /// Adds a piece of text. Text longer than <see cref="ScratchItem.MaxTextLength"/> is kept as a .txt file instead.
    /// </summary>
    /// <exception cref="ArgumentException">The text is empty.</exception>
    public async Task<string> AddTextAsync(string text, string device, CancellationToken ct = default)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Trim().Length == 0) throw new ArgumentException("There is no text to add.", nameof(text));
        if (text.Length > ScratchItem.MaxTextLength)
        {
            using var content = new MemoryStream(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
            var name = ScratchFormat.SafeFileName(ScratchFormat.FirstLine(text, 60), "Text") + ".txt";
            return await AddFileAsync(name, "text/plain", content, null, device, null, ct).ConfigureAwait(false);
        }
        return _items.Add(new ScratchItem
        {
            Kind = ScratchKind.Text,
            MediaType = "text/plain",
            Text = text,
            Size = text.Length,
            AddedAt = Now,
            AddedFrom = device,
        });
    }

    /// <summary>
    /// Encrypts a file into Scratch. It shows at once on this device and on the others after the next sync.
    /// </summary>
    /// <exception cref="BlobTooLargeException">Larger than the sync server accepts.</exception>
    public async Task<string> AddFileAsync(string name, string? mediaType, Stream content, byte[]? thumbnail, string device,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        name = ScratchFormat.SafeFileName(name);
        var blob = await _blobs.ImportAsync(content, progress, ct).ConfigureAwait(false);
        try
        {
            return _items.Add(new ScratchItem
            {
                Kind = ScratchKind.File,
                Name = name,
                MediaType = ScratchFormat.PickMediaType(mediaType, name),
                Size = blob.Size,
                Blob = blob,
                Thumbnail = thumbnail is { Length: > 0 and <= ScratchItem.MaxThumbnailBytes } ? thumbnail : null,
                AddedAt = Now,
                AddedFrom = device,
            });
        }
        catch
        {
            _blobs.Discard(blob.Id);
            throw;
        }
    }

    /// <exception cref="ArgumentException">The name is empty.</exception>
    public bool Rename(string id, string name)
    {
        if (name.Trim().Length == 0) throw new ArgumentException("A name cannot be empty.", nameof(name));
        var safe = ScratchFormat.SafeFileName(name);
        return Change(id, i => i.Kind != ScratchKind.File ? i : i with { Name = safe });
    }

    /// <summary>
    /// Drags a thing onto another one's place on the wall: it goes just before <paramref name="targetId"/> when moved up,
    /// just after it when moved down. Only the moved record changes (its key goes between its new neighbours), so two
    /// devices reordering at once never clash.
    /// </summary>
    /// <returns>False when either is gone, in the trash, or they are the same.</returns>
    public bool Move(string id, string targetId)
    {
        lock (_gate)
        {
            if (id == targetId) return false;
            var wall = Items(ScratchFilter.All);
            var from = IndexOf(wall, id);
            var to = IndexOf(wall, targetId);
            if (from < 0 || to < 0) return false;
            var others = wall.Where(i => i.Id != id).ToList();
            var at = IndexOf(others, targetId);
            // Moving down lands after the target, moving up before it.
            var slot = from < to ? at + 1 : at;
            var above = slot > 0 ? others[slot - 1].Value.Position : (double?)null;
            var below = slot < others.Count ? others[slot].Value.Position : (double?)null;
            var key = (above, below) switch
            {
                ({ } a, { } b) => (a + b) / 2,
                ({ } a, null) => a - 1000,
                (null, { } b) => b + 1000,
                _ => 0,
            };
            _items.Upsert(id, wall[from].Value with { SortKey = key });
            return true;
        }
    }

    private static int IndexOf(IReadOnlyList<SyncedItem<ScratchItem>> items, string id)
    {
        for (var i = 0; i < items.Count; i++)
            if (items[i].Id == id) return i;
        return -1;
    }

    public bool MoveToTrash(string id) => Change(id, i => i.Trashed ? i : i with { Trashed = true, TrashedAt = Now });

    public bool Restore(string id) => Change(id, i => i.Trashed ? i with { Trashed = false, TrashedAt = null } : i);

    /// <summary>
    /// Deletes a thing in the trash for good, on every device. Things outside the trash are never deleted. Its file
    /// leaves this device at once; the server copy is removed by the sync's clean-up.
    /// </summary>
    public bool DeleteForever(string id)
    {
        lock (_gate)
        {
            if (_items.Get(id) is not { Trashed: true } item || !_items.Delete(id)) return false;
            if (item.Blob is { } blob) _blobs.Discard(blob.Id);
            return true;
        }
    }

    /// <returns>How many things were deleted.</returns>
    public int EmptyTrash()
    {
        lock (_gate)
        {
            return Items(ScratchFilter.Trash).Count(i => DeleteForever(i.Id));
        }
    }

    // ---- Files ---------------------------------------------------------------------------------------------------

    /// <summary>Decrypts the file into <paramref name="destination"/>, downloading what this device does not have.</summary>
    /// <exception cref="BlobUnavailableException">Not on this device and not on the server (yet).</exception>
    public Task ReadAsync(ScratchItem item, Stream destination, IProgress<double>? progress = null, CancellationToken ct = default) =>
        item.Blob is { } blob
            ? _blobs.ReadAsync(blob, destination, progress, ct)
            : destination.WriteAsync(Encoding.UTF8.GetBytes(item.Text ?? ""), ct).AsTask();

    /// <summary>True while the file exists only on this device (it uploads with the next sync).</summary>
    public bool IsUploading(ScratchItem item) => item.Blob is { } blob && _blobs.IsPending(blob.Id);

    /// <summary>True when the file opens without the network.</summary>
    public bool IsOnThisDevice(ScratchItem item) => item.Blob is not { } blob || _blobs.IsCached(blob);

    /// <summary>Bytes Scratch's files take on this device (encrypted copies).</summary>
    public long BytesOnThisDevice() => _items.All().Select(i => i.Value.Blob).OfType<BlobRef>().Sum(_blobs.CachedBytes);

    /// <summary>
    /// Removes the local copies of files that are safely on the server; they download again when opened. Files that
    /// have not been uploaded yet are kept.
    /// </summary>
    /// <returns>The bytes freed.</returns>
    public long FreeUpSpace()
    {
        long freed = 0;
        foreach (var blob in _items.All().Select(i => i.Value.Blob).OfType<BlobRef>())
        {
            if (_blobs.IsPending(blob.Id)) continue;
            var bytes = _blobs.CachedBytes(blob);
            if (bytes == 0) continue;
            _blobs.Evict(blob.Id);
            freed += bytes - _blobs.CachedBytes(blob);
        }
        return freed;
    }

    private bool Change(string id, Func<ScratchItem, ScratchItem> change)
    {
        lock (_gate)
        {
            if (_items.Get(id) is not { } current) return false;
            var updated = change(current);
            if (updated != current) _items.Upsert(id, updated);
            return true;
        }
    }
}
