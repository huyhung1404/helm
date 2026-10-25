using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Modules.NovelReader.Scraping;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader;

/// <summary>
/// "Scrape from web" in Novel Reader's library: drives <see cref="NovelScraperService"/> and hands what came to the
/// novel view model's preview, for a new novel or for the new chapters of one already in the library ("Continue
/// download"). "Retry failed" runs the same scrape again: the chapters that came are kept on disk, so only the failed
/// ones are fetched.
/// </summary>
public sealed partial class NovelScraperViewModel : ObservableObject
{
    private readonly NovelScraperService _service;
    private readonly NovelReaderViewModel _novels;
    private readonly ILogger<NovelScraperViewModel> _logger;
    private CancellationTokenSource? _cts;

    /// <summary>The last scrape: its address, and the novel it continued (null for a new one).</summary>
    private (string Url, string? BookId)? _last;

    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _browserStatus = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private int _chapterDone;
    [ObservableProperty] private int _chapterTotal;

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);
    public bool IsIndeterminate => IsRunning && ChapterTotal == 0;

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnIsRunningChanged(bool value)
    {
        AutoCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        LogInCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        PickCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsIndeterminate));
    }
    partial void OnChapterTotalChanged(int value) => OnPropertyChanged(nameof(IsIndeterminate));

    public NovelScraperViewModel(NovelScraperService service, NovelReaderViewModel novels, ILogger<NovelScraperViewModel> logger)
    {
        _service = service;
        _novels = novels;
        _logger = logger;
        _novels.WebNovelSaved += (_, source) => _service.ForgetChapters(source);
    }

    /// <summary>
    /// Scrapes the novel whose chapter list is at <see cref="Url"/> and shows it for a look before it is added; a name
    /// rather than an address is looked for on the sites Helm knows.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task Auto()
    {
        var url = Url.Trim();
        if (url.Length == 0) return Task.CompletedTask;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? RunAsync(url, null) : Search();
    }

    /// <summary>Novels found by name; picking one scrapes it.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SearchHit> Results { get; } = [];

    [ObservableProperty] private bool _hasResults;

    /// <summary>Looks for the name in the box on the sites Helm knows (ixdzs8, Qidian, piaotia, xbanxia, biquge.tw).</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Search()
    {
        var query = Url.Trim();
        if (query.Length == 0)
        {
            StatusText = "Type the novel's name (or paste its address), then search.";
            return;
        }
        _cts = new CancellationTokenSource();
        IsRunning = true;
        ChapterTotal = 0;
        Results.Clear();
        HasResults = false;
        try
        {
            var prog = new Progress<(string Status, int Done, int Total)>(p =>
            {
                StatusText = p.Status;
                ChapterDone = p.Done;
                ChapterTotal = p.Total;
                Progress = p.Total > 0 ? (double)p.Done / p.Total : 0;
            });
            var hits = await Task.Run(() => _service.SearchAsync(query, prog, _cts.Token));
            foreach (var hit in hits) Results.Add(hit);
            HasResults = hits.Count > 0;
            StatusText = hits.Count == 0
                ? $"Nothing named “{query}” on ixdzs8, Qidian, piaotia or xbanxia (biquge.tw is left out when it asks for a check). Paste the novel's address instead."
                : $"{hits.Count} found: pick one to scrape it.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Search cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Searching for {Query} failed", query);
            StatusText = "Could not search: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Scrapes a novel picked from the search results.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task Pick(SearchHit? hit)
    {
        if (hit is null) return Task.CompletedTask;
        Url = hit.Url;
        Results.Clear();
        HasResults = false;
        return RunAsync(hit.Url, null);
    }

    /// <summary>Fetches the chapters a library novel lacks from its web source: new ones, and ones that failed before.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Continue(BookCardViewModel? card)
    {
        if (card is null || _novels.SourceUrlOf(card.Id) is not { } url) return;
        card.IsContinuing = true;
        try
        {
            await RunAsync(url, card.Id);
        }
        finally
        {
            card.IsContinuing = false;
        }
    }

    /// <summary>The same scrape again: chapters already downloaded are kept, so only the failed ones are fetched.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RetryFailed()
    {
        if (_novels.UpdatingBookId is { } bookId && _novels.SourceUrlOf(bookId) is { } url) return RunAsync(url, bookId);
        return _last is { } last ? RunAsync(last.Url, last.BookId) : Task.CompletedTask;
    }

    private bool CanStart() => !IsRunning;

    /// <summary>The first chapters of the novel being scraped, ready to read (<see cref="NovelScraperService.EarlyChapters"/>).</summary>
    private ScrapeResult? _early;

    /// <summary>The novel "Read now" added: the rest of the chapters go into it when the scrape ends.</summary>
    private string? _readingBookId;

    [ObservableProperty] private bool _canReadNow;

    /// <summary>
    /// Adds the novel with the chapters that came so far and opens it; the scrape goes on, and the rest of the chapters go
    /// into it when it ends (no preview).
    /// </summary>
    [RelayCommand]
    private async Task ReadNow()
    {
        if (_early is not { } first || _readingBookId is not null) return;
        CanReadNow = false;
        _readingBookId = await _novels.AddFromWebNowAsync(first);
        if (_readingBookId is not null && IsRunning)
            StatusText = "Reading the first chapters; the rest go into the novel when they are downloaded.";
    }

    /// <summary>The chapters that did not come, said after a run whose result was saved without a preview.</summary>
    private static string Failures(ScrapeResult result)
    {
        var failed = result.Count(ScrapeOutcome.Failed);
        var locked = result.Count(ScrapeOutcome.Locked);
        return (failed > 0 ? $" {failed} failed: press Continue on the novel to try them again." : "")
            + (locked > 0 ? $" {locked} are locked (the site wants a login or payment)." : "");
    }

    /// <summary>
    /// Opens the site in an Edge window on the scraper's own profile to log in (a novel's web source, or the address in
    /// the box): later scrapes are logged in, for paid chapters and sites that show only the first ones otherwise.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task LogIn(BookCardViewModel? card)
    {
        var url = card is null ? Url.Trim() : _novels.SourceUrlOf(card.Id) ?? "";
        if (url.Length == 0)
        {
            StatusText = "Paste the address of the site (or of the novel) first, then press Log in.";
            return;
        }
        _cts = new CancellationTokenSource();
        IsRunning = true;
        ChapterTotal = 0;
        try
        {
            var prog = new Progress<(string Status, int Done, int Total)>(p => StatusText = p.Status);
            await Task.Run(() => _service.LogInAsync(url, prog, _cts.Token));
            StatusText = "The login is kept: scrape (or Continue) now gets what your account can read.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped waiting for the login window.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the login window for {Url}", url);
            StatusText = "Could not open the login window: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunAsync(string url, string? bookId)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _last = (url, bookId);

        IsRunning = true;
        ChapterDone = 0;
        ChapterTotal = 0;
        Progress = 0;
        StatusText = "Looking for an open browser…";
        BrowserStatus = "";
        var title = _novels.IsAdding && !_novels.IsUpdating ? _novels.AddTitle : null;
        _early = null;
        _readingBookId = null;
        CanReadNow = false;
        // The first chapters of a new novel are in: it can be read while the rest download.
        var earlyProgress = new Progress<ScrapeResult>(r =>
        {
            if (!IsRunning || _readingBookId is not null) return;
            _early = r;
            CanReadNow = true;
        });
        Action<ScrapeResult>? early = bookId is null ? r => ((IProgress<ScrapeResult>)earlyProgress).Report(r) : null;

        try
        {
            var have = bookId is null ? null : await _novels.ReadChaptersAsync(bookId);
            var port = await _service.DetectBrowserPortAsync(ct);
            BrowserStatus = port.HasValue
                ? $"Using a new tab of the browser on port {port}"
                : "Using Helm's own Edge (a window opens only if the site asks for a check)";

            // Progress<T> made on the UI thread reports back on it.
            var prog = new Progress<(string Status, int Done, int Total)>(p =>
            {
                StatusText = p.Status;
                ChapterDone = p.Done;
                ChapterTotal = p.Total;
                Progress = p.Total > 0 ? (double)p.Done / p.Total : 0;
            });

            var result = await Task.Run(() => _service.ScrapeAsync(url, port, have, prog, ct, early), ct);
            result = result with { Cover = SmallCover(result.Cover) };
            IsRunning = false;
            CanReadNow = false;
            StatusText = Summary(result, bookId is not null);
            if (_readingBookId is { } reading)
            {
                // "Read now" stored the first chapters: the rest go into that novel, no preview.
                var added = await _novels.SaveNewChaptersAsync(reading, result);
                StatusText = $"All chapters are in: {added} more went into the novel you are reading." + Failures(result);
            }
            else if (bookId is null)
            {
                await _novels.PrepareAddFromWebAsync(result);
                // "Retry failed" keeps a title the user already changed.
                if (title is not null && _novels.IsAdding) _novels.AddTitle = title;
            }
            else
            {
                await _novels.PrepareUpdateAsync(bookId, result);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled. The chapters already downloaded are kept: scrape again to go on.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Novel scraping failed for {Url}", url);
            StatusText = "Could not scrape: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>The site's cover made small like a picked one (on the UI thread: it draws with WPF), or null.</summary>
    private byte[]? SmallCover(byte[]? picture)
    {
        if (picture is null) return null;
        try
        {
            return CoverPicture.Shrink(picture);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or System.IO.IOException
            or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            _logger.LogInformation(ex, "The site's cover is not a picture Windows can read");
            return null;
        }
    }

    /// <summary>What came, and what did not.</summary>
    private static string Summary(ScrapeResult result, bool continuing)
    {
        var got = result.Count(ScrapeOutcome.Got);
        var failed = result.Count(ScrapeOutcome.Failed);
        var locked = result.Count(ScrapeOutcome.Locked);
        var asked = result.Items.Count - result.Count(ScrapeOutcome.Skipped);
        if (continuing && asked == 0) return "No new chapters on the web page.";
        var text = continuing ? $"Got {got} of {asked} new chapters." : $"Got {got} of {asked} chapters.";
        if (failed > 0) text += $" {failed} failed.";
        if (locked > 0) text += $" {locked} are locked (the site wants a login or payment).";
        return got > 0 ? text + " Look through the preview, then save." : text;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private bool CanCancel() => IsRunning;
}
