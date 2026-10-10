using Helm.Core.Sync;
using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Library;

/// <summary>Which part of the library changed.</summary>
public enum NovelArea
{
    Books,
    Entries,
    Progress,
}

public sealed class NovelChangedEventArgs(NovelArea area, IReadOnlyList<string> ids, SyncChangeOrigin origin) : EventArgs
{
    public NovelArea Area { get; } = area;
    public IReadOnlyList<string> Ids { get; } = ids;
    public SyncChangeOrigin Origin { get; } = origin;
}

/// <summary>
/// The library on Helm Sync: each novel is a private "folder" made of its text (an encrypted blob), the names and
/// meanings saved for it, and where reading stopped. Everything works locally until sync is set up, then follows the
/// user to every device. Callable from any thread; <see cref="Changed"/> may come on a background thread.
/// </summary>
public sealed class NovelStore
{
    public const string BooksCollection = "novel.books";
    public const string EntriesCollection = "novel.entries";
    public const string ProgressCollection = "novel.progress";

    private readonly ISyncedCollection<NovelBook> _books;
    private readonly ISyncedCollection<NovelEntry> _entries;
    private readonly ISyncedCollection<NovelProgress> _progress;
    private readonly BlobStore _blobs;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Dictionary<string, NovelEntry>? _entryIndex;

    public NovelStore(ISyncedCollection<NovelBook> books, ISyncedCollection<NovelEntry> entries, ISyncedCollection<NovelProgress> progress,
        BlobStore blobs, TimeProvider? time = null)
    {
        _books = books;
        _entries = entries;
        _progress = progress;
        _blobs = blobs;
        _time = time ?? TimeProvider.System;
        _books.Changed += (_, e) => Changed?.Invoke(this, new NovelChangedEventArgs(NovelArea.Books, e.Ids, e.Origin));
        _entries.Changed += (_, e) =>
        {
            Reindex(e.Ids);
            Changed?.Invoke(this, new NovelChangedEventArgs(NovelArea.Entries, e.Ids, e.Origin));
        };
        _progress.Changed += (_, e) => Changed?.Invoke(this, new NovelChangedEventArgs(NovelArea.Progress, e.Ids, e.Origin));
        // An upload changes what a library card says ("Waiting to upload" → nothing).
        _blobs.Uploaded += (_, id) => Changed?.Invoke(this, new NovelChangedEventArgs(NovelArea.Books, [id], SyncChangeOrigin.Local));
    }

    /// <summary>A pull that deletes many novels at once is held for the user; the engine knows each novel's blob.</summary>
    public static SyncedCollectionOptions<NovelBook> BookOptions { get; } = new()
    {
        Name = BooksCollection,
        ConflictPolicy = SyncConflictPolicy.LastWriterWins,
        GuardDeletions = true,
        BlobReferences = book => new[] { book.Blob?.Id, book.Cover?.Id }.OfType<string>(),
    };

    /// <summary>Not guarded: deleting a novel deletes its names too, which would otherwise hold every other device.</summary>
    public static SyncedCollectionOptions<NovelEntry> EntryOptions { get; } = new()
    {
        Name = EntriesCollection,
        ConflictPolicy = SyncConflictPolicy.LastWriterWins,
    };

    public static SyncedCollectionOptions<NovelProgress> ProgressOptions { get; } = new()
    {
        Name = ProgressCollection,
        ConflictPolicy = SyncConflictPolicy.LastWriterWins,
    };

    public event EventHandler<NovelChangedEventArgs>? Changed;

    public DateTimeOffset Now => _time.GetUtcNow();

    // ---- Novels ------------------------------------------------------------------------------------------------------

    /// <summary>Every novel, the one read most recently first, then the newest.</summary>
    public IReadOnlyList<SyncedItem<NovelBook>> Books()
    {
        var progress = _progress.All().ToDictionary(p => p.Id, p => p.Value.UpdatedAt, StringComparer.Ordinal);
        return _books.All()
            .OrderByDescending(b => progress.TryGetValue(b.Id, out var at) ? at : b.Value.AddedAt)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();
    }

    public NovelBook? GetBook(string id) => _books.Get(id);

    /// <summary>
    /// Adds a novel: the text is encrypted into a blob at once (as UTF-8) and the record follows it to the other devices
    /// after the next sync.
    /// </summary>
    /// <exception cref="BlobTooLargeException">Larger than the sync server accepts.</exception>
    public async Task<string> AddBookAsync(string title, string fileName, string text, int chapterCount, string device,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        title = CleanTitle(title, fileName);
        using var content = new MemoryStream(TextFiles.Utf8.GetBytes(text));
        var blob = await _blobs.ImportAsync(content, progress, ct).ConfigureAwait(false);
        try
        {
            return _books.Add(new NovelBook
            {
                Title = title,
                FileName = Path.GetFileName(fileName),
                Blob = blob,
                Size = blob.Size,
                ChapterCount = chapterCount,
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

    /// <exception cref="BlobUnavailableException">Not on this device yet and sync cannot fetch it.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The stored text is damaged.</exception>
    public async Task<string> ReadBookTextAsync(string id, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var book = _books.Get(id) ?? throw new InvalidOperationException("This novel is no longer in the library.");
        if (book.Blob is not { } blob) return "";
        using var buffer = new MemoryStream((int)Math.Min(blob.Size, int.MaxValue));
        await _blobs.ReadAsync(blob, buffer, progress, ct).ConfigureAwait(false);
        return TextFiles.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>Added on this device and not uploaded yet.</summary>
    public bool IsUploading(NovelBook book) => book.Blob is { } blob && _blobs.IsPending(blob.Id);

    /// <summary>The text is on this device (opening it needs no download).</summary>
    public bool IsOnDevice(NovelBook book) => book.Blob is not { } blob || _blobs.IsCached(blob);

    public void RenameBook(string id, string title)
    {
        if (_books.Get(id) is not { } book) return;
        _books.Upsert(id, book with { Title = CleanTitle(title, book.FileName) });
    }

    /// <summary>Deletes a novel everywhere: its text, the names and meanings saved for it, and its progress.</summary>
    public void DeleteBook(string id)
    {
        var book = _books.Get(id);
        foreach (var entryId in EntryIdsOf(id)) _entries.Delete(entryId);
        _progress.Delete(id);
        _books.Delete(id);
        if (book?.Blob is { } blob) _blobs.Discard(blob.Id);
        if (book?.Cover is { } cover) _blobs.Discard(cover.Id);
    }

    // ---- Covers ----------------------------------------------------------------------------------------------------

    /// <summary>Sets a novel's cover picture (already made small by the platform); the old one is dropped.</summary>
    public async Task SetCoverAsync(string id, byte[] picture, CancellationToken ct = default)
    {
        if (_books.Get(id) is null) return;
        using var content = new MemoryStream(picture);
        var blob = await _blobs.ImportAsync(content, null, ct).ConfigureAwait(false);
        // Read again: the record may have changed (a rename) while the picture was stored.
        if (_books.Get(id) is not { } book)
        {
            _blobs.Discard(blob.Id);
            return;
        }
        _books.Upsert(id, book with { Cover = blob });
        if (book.Cover is { } old) _blobs.Discard(old.Id);
    }

    public void RemoveCover(string id)
    {
        if (_books.Get(id) is not { Cover: { } cover } book) return;
        _books.Upsert(id, book with { Cover = null });
        _blobs.Discard(cover.Id);
    }

    /// <summary>The cover picture, or null when there is none or it is not on this device yet.</summary>
    public async Task<byte[]?> ReadCoverAsync(string id, CancellationToken ct = default)
    {
        if (_books.Get(id)?.Cover is not { } cover) return null;
        try
        {
            return await _blobs.ReadAllAsync(cover, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BlobUnavailableException or System.Security.Cryptography.CryptographicException or IOException)
        {
            return null;
        }
    }

    // ---- Names and meanings ----------------------------------------------------------------------------------------

    /// <summary>The entries saved for one novel, or for all novels when <paramref name="bookId"/> is null.</summary>
    public IReadOnlyList<NovelEntry> Entries(string? bookId)
    {
        lock (_gate)
            return Index().Values.Where(e => e.BookId == bookId).OrderBy(e => e.Chinese, StringComparer.Ordinal).ToList();
    }

    public int EntryCount(string? bookId)
    {
        lock (_gate) return Index().Values.Count(e => e.BookId == bookId);
    }

    public NovelEntry? GetEntry(string? bookId, EntryKind kind, string chinese)
    {
        lock (_gate) return Index().GetValueOrDefault(EntryIds.For(bookId, kind, chinese));
    }

    /// <summary>Saves a name or meaning; saving the other kind for the same Chinese replaces it.</summary>
    public void SaveEntry(string? bookId, EntryKind kind, string chinese, string vietnamese)
    {
        chinese = ChineseText.NormalizeKey(chinese);
        if (chinese.Length == 0) throw new ArgumentException("There is no Chinese text to save.", nameof(chinese));
        var other = kind == EntryKind.Name ? EntryKind.Phrase : EntryKind.Name;
        _entries.Delete(EntryIds.For(bookId, other, chinese));
        _entries.Upsert(EntryIds.For(bookId, kind, chinese), new NovelEntry
        {
            BookId = bookId,
            Kind = kind,
            Chinese = chinese,
            Vietnamese = vietnamese.Trim(),
            UpdatedAt = Now,
        });
    }

    /// <summary>
    /// Removes what was saved for <paramref name="chinese"/> (name and meaning). A name the scan added is remembered as
    /// not a name, so the next scan leaves it out.
    /// </summary>
    public bool RemoveEntry(string? bookId, string chinese)
    {
        chinese = ChineseText.NormalizeKey(chinese);
        var wasAuto = GetEntry(bookId, EntryKind.Name, chinese)?.Auto == true;
        var removed = _entries.Delete(EntryIds.For(bookId, EntryKind.Name, chinese));
        removed |= _entries.Delete(EntryIds.For(bookId, EntryKind.Phrase, chinese));
        if (wasAuto && bookId is not null && _books.Get(bookId) is { } book && !book.IgnoredNames.Contains(chinese, StringComparer.Ordinal))
            _books.Upsert(bookId, book with { IgnoredNames = [.. book.IgnoredNames, chinese] });
        return removed;
    }

    // ---- Names found by the scan -----------------------------------------------------------------------------------

    /// <summary>
    /// Adds the names a scan found to a novel, marked as found: never over anything the user saved (for this novel or
    /// all novels, name or meaning), nor a name the user deleted after an earlier scan. Returns how many were added.
    /// </summary>
    public int SaveFoundNames(string bookId, IEnumerable<(string Chinese, string Vietnamese)> names)
    {
        if (_books.Get(bookId) is not { } book) return 0;
        var ignored = new HashSet<string>(book.IgnoredNames, StringComparer.Ordinal);
        var added = 0;
        foreach (var (raw, vietnamese) in names)
        {
            var chinese = ChineseText.NormalizeKey(raw);
            if (chinese.Length == 0 || vietnamese.Trim().Length == 0 || ignored.Contains(chinese)) continue;
            if (GetEntry(bookId, EntryKind.Name, chinese) is not null || GetEntry(bookId, EntryKind.Phrase, chinese) is not null
                || GetEntry(null, EntryKind.Name, chinese) is not null || GetEntry(null, EntryKind.Phrase, chinese) is not null) continue;
            _entries.Upsert(EntryIds.For(bookId, EntryKind.Name, chinese), new NovelEntry
            {
                BookId = bookId,
                Kind = EntryKind.Name,
                Chinese = chinese,
                Vietnamese = vietnamese.Trim(),
                UpdatedAt = Now,
                Auto = true,
            });
            added++;
        }
        return added;
    }

    /// <summary>Removes every name the scan added to a novel (the ones the user edited are theirs and stay).</summary>
    public int RemoveFoundNames(string bookId)
    {
        var ids = new List<string>();
        lock (_gate)
            ids.AddRange(Index().Where(e => e.Value.BookId == bookId && e.Value.Auto).Select(e => e.Key));
        foreach (var id in ids) _entries.Delete(id);
        return ids.Count;
    }

    public int FoundNameCount(string bookId)
    {
        lock (_gate) return Index().Values.Count(e => e.BookId == bookId && e.Auto);
    }

    /// <summary>The reader said a suggested word is not a name: later scans leave it out.</summary>
    public void IgnoreName(string bookId, string chinese)
    {
        chinese = ChineseText.NormalizeKey(chinese);
        if (_books.Get(bookId) is { } book && chinese.Length > 0 && !book.IgnoredNames.Contains(chinese, StringComparer.Ordinal))
            _books.Upsert(bookId, book with { IgnoredNames = [.. book.IgnoredNames, chinese] });
    }

    public void MarkNamesScanned(string bookId)
    {
        if (_books.Get(bookId) is { } book) _books.Upsert(bookId, book with { NamesScannedAt = Now });
    }

    /// <summary>Adds the lines of a Names.txt-style file; existing entries are replaced. Returns how many were added.</summary>
    public int ImportEntries(string? bookId, EntryKind kind, string text)
    {
        var count = 0;
        foreach (var (key, value) in Dictionaries.PhraseDictionary.ParseLines(text))
        {
            SaveEntry(bookId, kind, key, ChineseText.FirstMeaning(value));
            count++;
        }
        return count;
    }

    // ---- Progress --------------------------------------------------------------------------------------------------

    public NovelProgress? Progress(string bookId) => _progress.Get(bookId);

    public void SaveProgress(string bookId, int chapter, int paragraph, string device, int sentence = 0, int paragraphCount = 0)
    {
        if (_books.Get(bookId) is null) return;
        _progress.Upsert(bookId, new NovelProgress
        {
            Chapter = chapter,
            Paragraph = paragraph,
            Sentence = sentence,
            ParagraphCount = paragraphCount,
            UpdatedAt = Now,
            Device = device,
        });
    }

    /// <summary>The novel read most recently on any device, or the newest one when none was read.</summary>
    public string? LastReadBookId() => Books().FirstOrDefault()?.Id;

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private IEnumerable<string> EntryIdsOf(string bookId)
    {
        lock (_gate)
            return Index().Where(e => e.Value.BookId == bookId).Select(e => e.Key).ToList();
    }

    private Dictionary<string, NovelEntry> Index()
    {
        if (_entryIndex is not null) return _entryIndex;
        _entryIndex = new Dictionary<string, NovelEntry>(StringComparer.Ordinal);
        foreach (var item in _entries.All()) _entryIndex[item.Id] = item.Value;
        return _entryIndex;
    }

    private void Reindex(IReadOnlyList<string> ids)
    {
        lock (_gate)
        {
            if (_entryIndex is null) return;
            foreach (var id in ids)
            {
                if (_entries.Get(id) is { } entry) _entryIndex[id] = entry;
                else _entryIndex.Remove(id);
            }
        }
    }

    private static string CleanTitle(string title, string fileName)
    {
        title = title.Trim();
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (title.Length == 0) title = "Untitled";
        return title.Length > NovelBook.MaxTitleLength ? title[..NovelBook.MaxTitleLength] : title;
    }
}
