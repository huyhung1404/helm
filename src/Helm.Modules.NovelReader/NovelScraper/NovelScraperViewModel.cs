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

    /// <summary>Scrapes the novel whose chapter list is at <see cref="Url"/> and shows it for a look before it is added.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task Auto()
    {
        var url = Url.Trim();
        return url.Length == 0 ? Task.CompletedTask : RunAsync(url, null);
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

            var result = await Task.Run(() => _service.ScrapeAsync(url, port, have, prog, ct), ct);
            IsRunning = false;
            StatusText = Summary(result, bookId is not null);
            if (bookId is null)
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
