using Helm.Core.Sync;

namespace Helm.Modules.NovelReader.Reports;

/// <summary>
/// The reports of wrong translations on Helm Sync (one record each, so reports made on two devices both stay).
/// Callable from any thread; <see cref="Changed"/> may come on a background thread.
/// </summary>
public sealed class TranslationReportStore
{
    public const string CollectionName = "novel.reports";

    private readonly ISyncedCollection<TranslationReport> _reports;
    private readonly TimeProvider _time;

    public TranslationReportStore(ISyncedCollection<TranslationReport> reports, TimeProvider? time = null)
    {
        _reports = reports;
        _time = time ?? TimeProvider.System;
        _reports.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public static SyncedCollectionOptions<TranslationReport> Options { get; } = new()
    {
        Name = CollectionName,
        ConflictPolicy = SyncConflictPolicy.LastWriterWins,
    };

    public event EventHandler? Changed;

    /// <summary>Every report, the newest first; only one novel's when <paramref name="bookId"/> is given.</summary>
    public IReadOnlyList<SyncedItem<TranslationReport>> Reports(string? bookId = null) => _reports.All()
        .Where(r => bookId is null || r.Value.BookId == bookId)
        .OrderByDescending(r => r.Value.CreatedAt)
        .ThenByDescending(r => r.Id, StringComparer.Ordinal)
        .ToList();

    public TranslationReport? Get(string id) => _reports.Get(id);

    public int Count(string? bookId = null) => bookId is null ? _reports.All().Count : _reports.All().Count(r => r.Value.BookId == bookId);

    /// <summary>Saves a new report, stamped with the time and <paramref name="device"/>; returns its id.</summary>
    public string Add(TranslationReport report, string device)
    {
        var now = _time.GetUtcNow();
        return _reports.Add(report with { CreatedAt = now, UpdatedAt = now, Device = device });
    }

    /// <summary>Changes the right translation and the note of a report; false when it is gone.</summary>
    public bool Update(string id, string correct, string note)
    {
        if (_reports.Get(id) is not { } report) return false;
        _reports.Upsert(id, report with { Correct = correct.Trim(), Note = note.Trim(), UpdatedAt = _time.GetUtcNow() });
        return true;
    }

    /// <summary>Keeps an AI's suggestion on a report (its right translation stays as the reader wrote it); false when it is gone.</summary>
    public bool SaveSuggestion(string id, string translation, string note, string kind)
    {
        if (_reports.Get(id) is not { } report) return false;
        var now = _time.GetUtcNow();
        _reports.Upsert(id, report with
        {
            Suggestion = translation.Trim(),
            SuggestionNote = note.Trim(),
            ErrorKind = ErrorKinds.Normalize(kind),
            SuggestedAt = now,
            UpdatedAt = now,
        });
        return true;
    }

    /// <summary>Takes the AI's suggestion as the right translation; false when there is none.</summary>
    public bool UseSuggestion(string id)
    {
        if (_reports.Get(id) is not { Suggestion.Length: > 0 } report) return false;
        _reports.Upsert(id, report with { Correct = report.Suggestion, UpdatedAt = _time.GetUtcNow() });
        return true;
    }

    public bool Delete(string id) => _reports.Delete(id);
}
