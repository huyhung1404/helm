using Helm.Core.Services;
using Helm.Core.Sync;
using Helm.Modules.NovelReader.Books;
using Helm.Modules.NovelReader.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Helm's own look at web novels' sources, on the PC, in a hidden Edge, one novel at a time and never while the user
/// scrapes. A couple of minutes after Novel Reader starts and then every half hour, each novel that asks for it
/// (<see cref="NovelBook.AutoCheck"/>) and was last looked at six hours ago or more is looked at: its new chapters are
/// downloaded and saved when it says so (<see cref="NovelBook.AutoSave"/>), else counted for its card. A phone's request
/// (<see cref="NovelBook.UpdateRequestedAt"/>) is answered as soon as it arrives through sync. A Windows notification
/// tells what was found; a site that asks for a check is left for "Continue download", and said so when a phone asked.
/// </summary>
public sealed class NovelSourceWatcher(NovelStore store, NovelScraperService scraper, NovelReaderViewModel novels, IUiDispatcher ui,
    IServiceProvider services, ILogger<NovelSourceWatcher> logger) : IDisposable
{
    private static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Due = TimeSpan.FromHours(6);
    private static readonly TimeSpan AfterRequest = TimeSpan.FromSeconds(5);

    /// <summary>When a look that told nothing (a check, no answer) was last tried, so a site is not tried every half hour.</summary>
    private readonly Dictionary<string, DateTimeOffset> _tried = new(StringComparer.Ordinal);

    /// <summary>Phone requests already said to need a check here, so the notification shows once per request.</summary>
    private readonly HashSet<string> _toldCheck = new(StringComparer.Ordinal);

    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private int _running;

    public void Start()
    {
        if (_timer is not null) return;
        _cts = new CancellationTokenSource();
        _timer = new Timer(_ => _ = LookAsync(), null, FirstLook, Every);
        store.Changed += OnChanged;
    }

    public void Stop()
    {
        store.Changed -= OnChanged;
        _timer?.Dispose();
        _timer = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>A phone asked for new chapters: look in a few seconds rather than at the next half hour.</summary>
    private void OnChanged(object? sender, NovelChangedEventArgs e)
    {
        if (e.Area != NovelArea.Books || e.Origin != SyncChangeOrigin.Remote) return;
        if (e.Ids.Any(id => store.GetBook(id) is { } book && NovelStore.IsUpdateRequested(book))) _timer?.Change(AfterRequest, Every);
    }

    private async Task LookAsync()
    {
        if (_cts is not { } cts || Interlocked.Exchange(ref _running, 1) == 1) return;
        var ct = cts.Token;
        try
        {
            var now = store.Now;
            var due = store.Books()
                .Where(b => b.Value.SourceUrl is not null && (NovelStore.IsUpdateRequested(b.Value)
                    || (b.Value.AutoCheck && (b.Value.SourceCheckedAt is not { } at || now - at >= Due)
                        && (!_tried.TryGetValue(b.Id, out var tried) || now - tried >= Due))))
                // A phone waiting goes first.
                .OrderBy(b => NovelStore.IsUpdateRequested(b.Value) ? 0 : 1)
                .ToList();
            foreach (var (id, book, _) in due)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await LookAtAsync(id, book, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Offline, a novel deleted meanwhile, its text not downloadable: tried again with the next look.
                    logger.LogInformation(ex, "Looking at the web source of {Title} failed", book.Title);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task LookAtAsync(string id, NovelBook book, CancellationToken ct)
    {
        var requested = NovelStore.IsUpdateRequested(book);
        var text = await store.ReadBookTextAsync(id, ct: ct);
        var chapters = await Task.Run(() => ChapterSplitter.Split(text), ct);
        if (requested || book.AutoSave)
        {
            var result = await scraper.ScrapeQuietlyAsync(book.SourceUrl!, chapters, ct);
            if (result is null)
            {
                _tried[id] = store.Now;
                if (requested && book.UpdateRequestedAt is { } asked && _toldCheck.Add($"{id}:{asked:O}"))
                    Notify(book.Title, "Your phone asked for its new chapters, but the site wants a check first. Press Continue on the novel in Novel Reader.");
                return;
            }
            Task<int>? saving = null;
            await ui.InvokeAsync(() => saving = novels.SaveNewChaptersAsync(id, result));
            var added = await saving!;
            if (added > 0) Notify(book.Title, added == 1 ? "1 new chapter saved." : $"{added} new chapters saved.");
            return;
        }

        var count = await scraper.CountNewAsync(book.SourceUrl!, chapters, ct);
        if (count is not { } n)
        {
            _tried[id] = store.Now;
            return;
        }
        logger.LogInformation("{Title} has {Count} new chapters on the web", book.Title, n);
        await ui.InvokeAsync(() => store.MarkSourceChecked(id, n));
        if (n > book.SourceNewChapters)
            Notify(book.Title, n == 1 ? "1 new chapter on the web. Press Continue on the novel to get it." : $"{n} new chapters on the web. Press Continue on the novel to get them.");
    }

    /// <summary>
    /// A Windows notification; clicking it opens Novel Reader. Resolved when shown: the notifications and the shell
    /// depend on the module list, which holds Novel Reader.
    /// </summary>
    private void Notify(string title, string message)
    {
        try
        {
            services.GetRequiredService<IUserNotifications>().Show(title, message,
                () => services.GetRequiredService<IShellNavigation>().ShowPage(typeof(NovelReaderContentPage)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            logger.LogInformation(ex, "Could not show a notification for {Title}", title);
        }
    }

    public void Dispose() => Stop();
}
