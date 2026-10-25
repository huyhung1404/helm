using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Sync;
using Helm.Modules.NovelReader.Books;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Library;
using Helm.Modules.NovelReader.Scraping;
using Helm.Modules.NovelReader.Text;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Adding a novel (from a file or scraped from the web) and continuing a web novel's download: what is about to be
/// stored is shown first, chapter by chapter in the reading language, and only stored when the user says so. A
/// continued download puts its new chapters into the novel and moves the reading place with the chapters before it.
/// </summary>
public sealed partial class NovelReaderViewModel
{
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private PreviewChapterItem? _selectedPreviewChapter;
    [ObservableProperty] private bool _previewShowChinese;
    [ObservableProperty] private int _previewFailedCount;

    private string? _addPath;
    private string? _addText;
    private int _addChapterCount;

    /// <summary>A novel from the web is stored in parts (see <see cref="NovelBook.Parts"/>), a file as one text.</summary>
    private IReadOnlyList<TextPart>? _addParts;
    private string? _addSourceUrl;

    /// <summary>The cover the site shows (already made small), set on the novel when it has none.</summary>
    private byte[]? _addCover;
    private string? _updateBookId;
    private IReadOnlyList<int>? _updateMap;
    private Converter? _previewConverter;
    private int _previewListVersion;
    private int _previewLineVersion;

    /// <summary>The chapters about to be stored (new ones only, when continuing a download), failed and locked ones too.</summary>
    public ObservableCollection<PreviewChapterItem> PreviewChapters { get; } = [];

    /// <summary>The selected chapter, converted like the reader shows it.</summary>
    public ObservableCollection<PreviewLine> PreviewLines { get; } = [];

    public bool HasPreviewFailures => PreviewFailedCount > 0;

    public string AddButtonText => IsUpdating ? "Save to the novel" : "Add to library";

    /// <summary>A novel from the web was stored (its source address): the chapters kept while scraping it can go.</summary>
    public event EventHandler<string>? WebNovelSaved;

    /// <summary>The book that the open preview continues (its id), or null when the preview is of a new novel.</summary>
    public string? UpdatingBookId => _updateBookId;

    partial void OnIsUpdatingChanged(bool value) => OnPropertyChanged(nameof(AddButtonText));

    partial void OnPreviewFailedCountChanged(int value) => OnPropertyChanged(nameof(HasPreviewFailures));

    partial void OnSelectedPreviewChapterChanged(PreviewChapterItem? value) => _ = ShowPreviewChapterAsync(value);

    partial void OnPreviewShowChineseChanged(bool value) => _ = ShowPreviewChapterAsync(SelectedPreviewChapter);

    /// <summary>Step 1 of adding a novel: read the file and show what was found, with a title to edit.</summary>
    public async Task PrepareAddAsync(string path)
    {
        Message = null;
        IsBusy = true;
        BusyText = "Reading the file…";
        try
        {
            var text = await Task.Run(() => TextFiles.ReadAllText(path));
            // A Traditional Chinese file is kept in Simplified, which the dictionaries read far better.
            var convert = ConvertTraditional;
            text = await Task.Run(() => convert && TraditionalChinese.Looks(text) ? TraditionalChinese.ToSimplified(text) : text);
            var chapters = await Task.Run(() => ChapterSplitter.Split(text));
            ResetAdd();
            _addPath = path;
            _addText = text;
            _addChapterCount = chapters.Count;
            AddTitle = Path.GetFileNameWithoutExtension(path);
            var first = chapters.Count > 0 ? chapters[0].Title : "";
            AddSummary = string.Create(CultureInfo.InvariantCulture, $"{chapters.Count} chapters, {text.Length:N0} characters. First chapter: {first}");
            await FillPreviewAsync(chapters.Select(c => new PreviewChapterItem(c.Title, ScrapeOutcome.Got, c, isNew: false)).ToList());
            IsAdding = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "Could not read the file: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Step 1 of adding a novel scraped from the web: every chapter of its list, the failed and locked ones marked.</summary>
    public async Task PrepareAddFromWebAsync(ScrapeResult result)
    {
        Message = null;
        IsBusy = true;
        BusyText = "Getting the preview ready…";
        try
        {
            var got = result.Items.Where(i => i.Chapter is not null).Select(i => i.Chapter!).ToList();
            var parts = await Task.Run(() => ScrapedBook.Parts(got));
            ResetAdd();
            // Only the name of this file is kept: a title with / or : in it must not lose its start.
            _addPath = string.Concat(result.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".txt";
            _addParts = parts;
            _addChapterCount = got.Count;
            _addSourceUrl = result.SourceUrl;
            _addCover = result.Cover;
            AddTitle = result.Title;
            AddSummary = Summary(result, got.Count, $"{got.Count} of {result.Items.Count} chapters");
            await FillPreviewAsync(result.Items.Select(i => new PreviewChapterItem(i.Title, i.Outcome, ToChapter(i.Chapter), isNew: false)).ToList());
            IsAdding = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not prepare a scraped novel");
            Message = "Could not process the novel: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Step 1 of continuing a novel's download: its new chapters (and those that failed or are locked) to look at before
    /// they are put in. Nothing new: says so and records the check.
    /// </summary>
    public async Task PrepareUpdateAsync(string bookId, ScrapeResult result)
    {
        if (_store.GetBook(bookId) is not { } book) return;
        Message = null;
        IsBusy = true;
        BusyText = "Getting the preview ready…";
        try
        {
            var existing = await ReadChaptersAsync(bookId);
            var merged = await Task.Run(() => BookUpdate.Merge(existing, result.Items));
            var fresh = result.Items.Where(i => i.Outcome != ScrapeOutcome.Skipped).ToList();
            if (merged.Added.Count == 0)
            {
                _store.MarkSourceChecked(bookId, 0);
                RefreshLibrary();
                var notGot = fresh.Count;
                Message = notGot == 0
                    ? $"“{book.Title}” has every chapter of its web page."
                    : $"No chapter came for “{book.Title}”: {notGot} could not be downloaded (failed or locked). Try again later.";
                return;
            }
            var parts = await Task.Run(() => ScrapedBook.Parts([.. merged.Chapters.Select(c => new ScrapedChapter(c.Title, c.Paragraphs))]));
            ResetAdd();
            _updateBookId = bookId;
            _updateMap = merged.NewIndexOfOld;
            _addPath = book.FileName;
            _addParts = parts;
            _addChapterCount = merged.Chapters.Count;
            _addSourceUrl = result.SourceUrl;
            _addCover = result.Cover;
            AddTitle = book.Title;
            AddSummary = Summary(result, merged.Added.Count, $"{merged.Added.Count} new chapters for a novel of {existing.Count}");
            IsUpdating = true;
            await FillPreviewAsync(fresh.Select(i => new PreviewChapterItem(i.Title, i.Outcome, ToChapter(i.Chapter), isNew: i.Outcome == ScrapeOutcome.Got)).ToList());
            IsAdding = true;
        }
        catch (Exception ex) when (ex is BlobUnavailableException or IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning(ex, "Could not prepare the new chapters of {Id}", bookId);
            Message = "Could not read the novel to add its new chapters: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The web page a library novel's chapters are listed on, or null.</summary>
    public string? SourceUrlOf(string bookId) => _store.GetBook(bookId)?.SourceUrl;

    /// <summary>
    /// Adds a novel from the web at once, without a preview, and opens it: the first chapters of a novel still
    /// downloading ("Read now"); the rest go in with <see cref="SaveNewChaptersAsync"/> when they are in. Returns its id.
    /// </summary>
    public async Task<string?> AddFromWebNowAsync(ScrapeResult result)
    {
        Message = null;
        IsBusy = true;
        BusyText = "Adding the novel…";
        try
        {
            var got = result.Items.Where(i => i.Chapter is not null).Select(i => i.Chapter!).ToList();
            var parts = await Task.Run(() => ScrapedBook.Parts(got));
            var fileName = string.Concat(result.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".txt";
            var id = await _store.AddBookAsync(result.Title, fileName, parts, _device.DeviceName, new Progress<double>(p => BusyProgress = p),
                sourceUrl: result.SourceUrl);
            if (result.Cover is { } cover) await _store.SetCoverAsync(id, cover);
            RefreshLibrary();
            IsBusy = false;
            await OpenBookAsync(id);
            return id;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or BlobTooLargeException)
        {
            _logger.LogWarning(ex, "Could not add the first chapters of a novel");
            Message = "Could not add the novel: " + ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Puts the chapters that came into a library novel at once, without a preview (new chapters Helm found by itself,
    /// a phone's request, the rest of a novel being read while it downloads): its text is stored again (only the parts
    /// that changed), the reading place follows its chapter, and the site's cover is kept when the novel has none. The
    /// novel open in the reader gets its new chapters at the end of its list (reading aloud goes on); chapters put in
    /// before show the next time it is opened. Returns how many chapters were added.
    /// </summary>
    public async Task<int> SaveNewChaptersAsync(string bookId, ScrapeResult result)
    {
        if (_store.GetBook(bookId) is not { } book) return 0;
        var existing = await ReadChaptersAsync(bookId);
        var merged = await Task.Run(() => BookUpdate.Merge(existing, result.Items));
        if (merged.Added.Count == 0)
        {
            _store.MarkSourceChecked(bookId, 0);
            RefreshLibrary();
            return 0;
        }
        if (OpenBookId == bookId) FlushProgress();
        var parts = await Task.Run(() => ScrapedBook.Parts([.. merged.Chapters.Select(c => new ScrapedChapter(c.Title, c.Paragraphs))]));
        await _store.ReplaceBookTextAsync(bookId, parts);
        if (_store.Progress(bookId) is { } place && place.Chapter >= 0 && place.Chapter < merged.NewIndexOfOld.Count)
            _store.MoveProgressChapter(bookId, merged.NewIndexOfOld[place.Chapter]);
        if (result.Cover is { } cover && book.Cover is null)
        {
            try
            {
                await _store.SetCoverAsync(bookId, cover);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _logger.LogInformation(ex, "Could not keep the cover of {Id}", bookId);
            }
        }
        if (OpenBookId == bookId)
        {
            var appended = merged.Added.All(i => i >= existing.Count);
            if (appended)
            {
                var text = string.Join("\n\n", parts.Select(p => p.Text));
                var chapters = await Task.Run(() => ChapterSplitter.Split(text));
                if (OpenBookId == bookId && chapters.Count > _chapters.Count)
                {
                    var from = _chapters.Count;
                    _chapters = chapters;
                    for (var i = from; i < chapters.Count; i++) Chapters.Add(new ChapterItem(i, chapters[i].Title));
                    ConvertChapterTitles();
                }
            }
            else
            {
                Message = $"{merged.Added.Count} chapters were put into this novel; open it again to see them in their place.";
            }
        }
        RefreshLibrary();
        _logger.LogInformation("Saved {Count} new chapters into {Title}", merged.Added.Count, book.Title);
        return merged.Added.Count;
    }

    /// <summary>A novel's chapters as the reader splits them (downloading its text when it is not on this device).</summary>
    public async Task<IReadOnlyList<Chapter>> ReadChaptersAsync(string bookId)
    {
        var text = await _store.ReadBookTextAsync(bookId);
        return await Task.Run(() => ChapterSplitter.Split(text));
    }

    /// <summary>Step 2: store the novel (encrypted, synced), or put the new chapters into it, and open it.</summary>
    [RelayCommand]
    private async Task ConfirmAdd()
    {
        if (_addPath is null || (_addText is null && _addParts is null)) return;
        IsBusy = true;
        BusyText = IsUpdating ? "Saving the new chapters…" : "Adding the novel…";
        try
        {
            var progress = new Progress<double>(p => BusyProgress = p);
            var source = _addSourceUrl;
            string id;
            if (_updateBookId is { } bookId)
            {
                id = bookId;
                FlushProgress();
                await _store.ReplaceBookTextAsync(id, _addParts ?? [new TextPart(_addText ?? "", _addChapterCount)], progress);
                // Chapters put in before the reading place push it on, so it stays on the same chapter.
                if (_store.Progress(id) is { } place && _updateMap is { } map && place.Chapter >= 0 && place.Chapter < map.Count)
                    _store.MoveProgressChapter(id, map[place.Chapter]);
                if (_store.GetBook(id) is { } book && book.Title != AddTitle.Trim()) _store.RenameBook(id, AddTitle);
            }
            else if (_addParts is { } parts)
            {
                id = await _store.AddBookAsync(AddTitle, Path.GetFileName(_addPath), parts, _device.DeviceName, progress, sourceUrl: _addSourceUrl);
            }
            else
            {
                id = await _store.AddBookAsync(AddTitle, Path.GetFileName(_addPath), _addText ?? "", _addChapterCount, _device.DeviceName,
                    progress, sourceUrl: _addSourceUrl);
            }
            if (_addCover is { } cover && _store.GetBook(id) is { Cover: null })
            {
                try
                {
                    await _store.SetCoverAsync(id, cover);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    _logger.LogInformation(ex, "Could not keep the cover of {Id}", id);
                }
            }
            if (source is not null) WebNovelSaved?.Invoke(this, source);
            CancelAdd();
            RefreshLibrary();
            IsBusy = false;
            await OpenBookAsync(id);
        }
        catch (BlobTooLargeException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not add a novel");
            Message = (IsUpdating ? "Could not save the new chapters: " : "Could not add the novel: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsAdding = false;
        ResetAdd();
        AddTitle = "";
        AddSummary = "";
    }

    private void ResetAdd()
    {
        _addPath = null;
        _addText = null;
        _addParts = null;
        _addSourceUrl = null;
        _addCover = null;
        _updateBookId = null;
        _updateMap = null;
        _previewConverter = null;
        _previewListVersion++;
        _previewLineVersion++;
        IsUpdating = false;
        PreviewFailedCount = 0;
        SelectedPreviewChapter = null;
        PreviewChapters.Clear();
        PreviewLines.Clear();
    }

    private static string Summary(ScrapeResult result, int got, string head)
    {
        var failed = result.Count(ScrapeOutcome.Failed);
        var locked = result.Count(ScrapeOutcome.Locked);
        var text = head + ".";
        if (failed > 0) text += $" {failed} failed (marked in red): “Retry failed” downloads only those.";
        if (locked > 0) text += $" {locked} are locked (the site wants a login or payment).";
        return got == 0 ? text : text + " Look through it, then save.";
    }

    private static Chapter? ToChapter(ScrapedChapter? chapter) => chapter is null ? null : new Chapter(chapter.Title, chapter.Paragraphs);

    /// <summary>Lists the chapters with titles in the reading language and shows the first one that has text.</summary>
    private async Task FillPreviewAsync(IReadOnlyList<PreviewChapterItem> items)
    {
        var version = _previewListVersion;
        PreviewChapters.Clear();
        foreach (var item in items) PreviewChapters.Add(item);
        PreviewFailedCount = items.Count(i => i.Outcome == ScrapeOutcome.Failed);
        SelectedPreviewChapter = items.FirstOrDefault(i => i.Chapter is not null) ?? items.FirstOrDefault();
        // Shown before the preview opens (the change above starts it too; this later run wins).
        await ShowPreviewChapterAsync(SelectedPreviewChapter);

        var converter = await PreviewConverterAsync();
        var mode = PreviewMode;
        var titles = await Task.Run(() => items.Select(i => converter.ConvertLine(i.Title, mode).Text).ToList());
        if (version != _previewListVersion) return;
        for (var i = 0; i < items.Count; i++) items[i].DisplayTitle = titles[i];
    }

    private async Task ShowPreviewChapterAsync(PreviewChapterItem? item)
    {
        var version = ++_previewLineVersion;
        PreviewLines.Clear();
        if (item is null) return;
        if (item.Chapter is not { } chapter)
        {
            PreviewLines.Add(new PreviewLine(item.DisplayTitle, item.Title, true));
            PreviewLines.Add(new PreviewLine(item.Outcome == ScrapeOutcome.Locked
                ? "This chapter is locked on the site (it wants a login or payment), so it is not saved."
                : "This chapter did not download, so it is not saved. “Retry failed” tries it again.", "", false));
            return;
        }
        var converter = await PreviewConverterAsync();
        var mode = PreviewMode;
        var chinese = PreviewShowChinese;
        var lines = await Task.Run(() => chapter.Paragraphs.Select(p => (p, false)).Prepend((chapter.Title, true))
            .Select(x => new PreviewLine(converter.ConvertLine(x.Item1, mode).Text, chinese ? x.Item1 : "", x.Item2)).ToList());
        if (version != _previewLineVersion) return;
        foreach (var line in lines) PreviewLines.Add(line);
    }

    /// <summary>The preview reads like the reader, without study mode's four rows.</summary>
    private ConvertMode PreviewMode => Mode == ConvertMode.Study ? ConvertMode.VietPhrase : Mode;

    /// <summary>The dictionaries with the names saved for all novels, and for the novel being continued.</summary>
    private async Task<Converter> PreviewConverterAsync()
    {
        if (_previewConverter is { } ready) return ready;
        DictionarySet set;
        try
        {
            set = _dictionaries.HasFiles ? await _dictionaries.EnsureLoadedAsync() : DictionarySet.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not read the dictionaries for the preview");
            set = DictionarySet.Empty;
        }
        var layers = UserLayers.Build(_store, _updateBookId);
        return _previewConverter = new Converter(set, layers.BookNames, layers.UserNames, layers.Phrases, PrioritizeNames);
    }

    // ---- A novel's web source ----------------------------------------------------------------------------------------

    [RelayCommand]
    private void StartEditSource(BookCardViewModel? card)
    {
        if (card is null) return;
        card.SourceDraft = _store.GetBook(card.Id)?.SourceUrl ?? "";
        card.IsEditingSource = true;
    }

    [RelayCommand]
    private void SaveSource(BookCardViewModel? card)
    {
        if (card is null) return;
        var url = card.SourceDraft.Trim();
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            Message = "The web source must be a web address (https://…) of the novel's chapter list.";
            return;
        }
        _store.SetSource(card.Id, url, _store.GetBook(card.Id)?.AutoCheck ?? false);
        card.IsEditingSource = false;
        RefreshLibrary();
    }

    [RelayCommand]
    private void CancelEditSource(BookCardViewModel? card)
    {
        if (card is not null) card.IsEditingSource = false;
    }

    /// <summary>Turns Helm's own look for new chapters on or off for a novel with a web source.</summary>
    [RelayCommand]
    private void ToggleAutoCheck(BookCardViewModel? card)
    {
        if (card is null || _store.GetBook(card.Id) is not { SourceUrl: { } url } book) return;
        _store.SetSource(card.Id, url, !book.AutoCheck);
        RefreshLibrary();
    }

    /// <summary>New chapters Helm finds by itself are saved at once, or shown first (the default).</summary>
    [RelayCommand]
    private void ToggleAutoSave(BookCardViewModel? card)
    {
        if (card is null || _store.GetBook(card.Id) is not { SourceUrl: not null } book) return;
        _store.SetAutoSave(card.Id, !book.AutoSave);
        RefreshLibrary();
    }

    /// <summary>On the phone (no browser): asks the PC, through sync, to download and save the novel's new chapters.</summary>
    [RelayCommand]
    private void RequestUpdate(BookCardViewModel? card)
    {
        if (card is null) return;
        _store.RequestUpdate(card.Id);
        RefreshLibrary();
    }

    /// <summary>The card's line about the web source: new chapters found, when it was looked at, whether Helm looks by itself.</summary>
    private static string SourceStatus(NovelBook book, DateTimeOffset now)
    {
        if (book.SourceUrl is null) return "";
        if (NovelStore.IsUpdateRequested(book)) return "Waiting for the PC to download the new chapters (Helm must be open there)";
        var checkedAt = book.SourceCheckedAt is { } at ? $"checked {NovelFormat.Ago(at, now)}" : "not checked yet";
        var found = book.SourceNewChapters > 0 ? $"{book.SourceNewChapters} new chapters on the web · " : "";
        var self = book.AutoCheck ? book.AutoSave ? " · checks and saves by itself" : " · checks by itself" : "";
        return $"{found}{checkedAt}{self}";
    }
}
