using System.Text;
using Helm.Core.Sync;

namespace Helm.Modules.Stash;

/// <summary>
/// The stash on top of Helm Sync. A record per thing: text lives in the record, a file is encrypted into a blob at
/// once (the plaintext never touches Helm's folders) and uploaded by the sync engine before the record. Deleting moves
/// to the trash; only deleting from the trash removes a thing for good, on every device. Callable from any thread;
/// <see cref="Changed"/> may be raised on a background thread after a sync or an upload.
/// </summary>
public sealed class StashStore
{
    public const string Collection = "stash.items";

    private readonly ISyncedCollection<StashItem> _items;
    private readonly BlobStore _blobs;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public StashStore(ISyncedCollection<StashItem> items, BlobStore blobs, TimeProvider? time = null)
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
    public static SyncedCollectionOptions<StashItem> Options { get; } = new()
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

    public StashItem? Get(string id) => _items.Get(id);

    /// <summary>What a filter shows: newest first, or the most recently deleted first in the trash.</summary>
    public IReadOnlyList<SyncedItem<StashItem>> Items(StashFilter filter)
    {
        var all = _items.All();
        if (filter == StashFilter.Trash)
            return all.Where(i => i.Value.Trashed).OrderByDescending(i => i.Value.TrashedAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        return all.Where(i => !i.Value.Trashed && Matches(i.Value, filter))
            .OrderByDescending(i => i.Value.AddedAt).ThenByDescending(i => i.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>How many things each filter shows.</summary>
    public IReadOnlyDictionary<StashFilter, int> Counts()
    {
        var all = _items.All().Select(i => i.Value).ToList();
        var live = all.Where(i => !i.Trashed).ToList();
        return new Dictionary<StashFilter, int>
        {
            [StashFilter.All] = live.Count,
            [StashFilter.Media] = live.Count(i => Matches(i, StashFilter.Media)),
            [StashFilter.Files] = live.Count(i => Matches(i, StashFilter.Files)),
            [StashFilter.Text] = live.Count(i => Matches(i, StashFilter.Text)),
            [StashFilter.Trash] = all.Count - live.Count,
        };
    }

    public static bool Matches(StashItem item, StashFilter filter) => filter switch
    {
        StashFilter.Media => item.IsMedia,
        StashFilter.Files => item.Kind == StashKind.File && !item.IsMedia,
        StashFilter.Text => item.Kind == StashKind.Text,
        StashFilter.Trash => item.Trashed,
        _ => true,
    };

    /// <summary>
    /// Adds a piece of text. Text longer than <see cref="StashItem.MaxTextLength"/> is kept as a .txt file instead.
    /// </summary>
    /// <exception cref="ArgumentException">The text is empty.</exception>
    public async Task<string> AddTextAsync(string text, string device, CancellationToken ct = default)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Trim().Length == 0) throw new ArgumentException("There is no text to add.", nameof(text));
        if (text.Length > StashItem.MaxTextLength)
        {
            using var content = new MemoryStream(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
            var name = StashFormat.SafeFileName(StashFormat.FirstLine(text, 60), "Text") + ".txt";
            return await AddFileAsync(name, "text/plain", content, null, device, null, ct).ConfigureAwait(false);
        }
        return _items.Add(new StashItem
        {
            Kind = StashKind.Text,
            MediaType = "text/plain",
            Text = text,
            Size = text.Length,
            AddedAt = Now,
            AddedFrom = device,
        });
    }

    /// <summary>
    /// Encrypts a file into the stash. It shows at once on this device and on the others after the next sync.
    /// </summary>
    /// <exception cref="BlobTooLargeException">Larger than the sync server accepts.</exception>
    public async Task<string> AddFileAsync(string name, string? mediaType, Stream content, byte[]? thumbnail, string device,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        name = StashFormat.SafeFileName(name);
        var blob = await _blobs.ImportAsync(content, progress, ct).ConfigureAwait(false);
        try
        {
            return _items.Add(new StashItem
            {
                Kind = StashKind.File,
                Name = name,
                MediaType = StashFormat.PickMediaType(mediaType, name),
                Size = blob.Size,
                Blob = blob,
                Thumbnail = thumbnail is { Length: > 0 and <= StashItem.MaxThumbnailBytes } ? thumbnail : null,
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
        var safe = StashFormat.SafeFileName(name);
        return Change(id, i => i.Kind != StashKind.File ? i : i with { Name = safe });
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
            return Items(StashFilter.Trash).Count(i => DeleteForever(i.Id));
        }
    }

    // ---- Files ---------------------------------------------------------------------------------------------------

    /// <summary>Decrypts the file into <paramref name="destination"/>, downloading what this device does not have.</summary>
    /// <exception cref="BlobUnavailableException">Not on this device and not on the server (yet).</exception>
    public Task ReadAsync(StashItem item, Stream destination, IProgress<double>? progress = null, CancellationToken ct = default) =>
        item.Blob is { } blob
            ? _blobs.ReadAsync(blob, destination, progress, ct)
            : destination.WriteAsync(Encoding.UTF8.GetBytes(item.Text ?? ""), ct).AsTask();

    /// <summary>True while the file exists only on this device (it uploads with the next sync).</summary>
    public bool IsUploading(StashItem item) => item.Blob is { } blob && _blobs.IsPending(blob.Id);

    /// <summary>True when the file opens without the network.</summary>
    public bool IsOnThisDevice(StashItem item) => item.Blob is not { } blob || _blobs.IsCached(blob);

    /// <summary>Bytes the stash's files take on this device (encrypted copies).</summary>
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

    private bool Change(string id, Func<StashItem, StashItem> change)
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
