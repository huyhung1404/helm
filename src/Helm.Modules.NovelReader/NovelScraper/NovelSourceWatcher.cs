using Helm.Core.Services;
using Helm.Modules.NovelReader.Books;
using Helm.Modules.NovelReader.Library;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Helm's own look for new chapters, for the novels the user asked it of (<see cref="NovelBook.AutoCheck"/>): a couple of
/// minutes after Novel Reader starts and then every half hour, each such novel last looked at six hours ago or more has
/// its web list read in a hidden Edge, one at a time, and the card says how many chapters are new. Nothing is
/// downloaded or shown: a site that asks for a check is left for "Continue download", and a run of the scraper the user
/// started always goes first.
/// </summary>
public sealed class NovelSourceWatcher(NovelStore store, NovelScraperService scraper, IUiDispatcher ui, ILogger<NovelSourceWatcher> logger) : IDisposable
{
    private static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Due = TimeSpan.FromHours(6);

    /// <summary>When a look that told nothing (a check, no answer) was last tried, so a site is not tried every half hour.</summary>
    private readonly Dictionary<string, DateTimeOffset> _tried = new(StringComparer.Ordinal);
    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private int _running;

    public void Start()
    {
        if (_timer is not null) return;
        _cts = new CancellationTokenSource();
        _timer = new Timer(_ => _ = LookAsync(), null, FirstLook, Every);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LookAsync()
    {
        if (_cts is not { } cts || Interlocked.Exchange(ref _running, 1) == 1) return;
        var ct = cts.Token;
        try
        {
            var now = store.Now;
            var due = store.Books()
                .Where(b => b.Value.AutoCheck && b.Value.SourceUrl is not null
                    && (b.Value.SourceCheckedAt is not { } at || now - at >= Due)
                    && (!_tried.TryGetValue(b.Id, out var tried) || now - tried >= Due))
                .ToList();
            foreach (var (id, book, _) in due)
            {
                ct.ThrowIfCancellationRequested();
                _tried[id] = now;
                var text = await store.ReadBookTextAsync(id, ct: ct);
                var chapters = await Task.Run(() => ChapterSplitter.Split(text), ct);
                var count = await scraper.CountNewAsync(book.SourceUrl!, chapters, ct);
                if (count is not { } n) continue;
                logger.LogInformation("{Title} has {Count} new chapters on the web", book.Title, n);
                await ui.InvokeAsync(() => store.MarkSourceChecked(id, n));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Offline, a novel deleted meanwhile, its text not downloadable: tried again with the next look.
            logger.LogInformation(ex, "Looking for new chapters stopped");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose() => Stop();
}
