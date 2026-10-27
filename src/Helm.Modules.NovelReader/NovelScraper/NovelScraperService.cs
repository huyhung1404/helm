using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Modules.NovelReader.Books;
using Helm.Modules.NovelReader.Scraping;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.NovelReader;

/// <summary>
/// Scrapes a Chinese novel from its chapter-list page through the DevTools protocol: in a new tab of a browser already
/// open on a standard CDP port, or in an Edge of Helm's own (hidden, its own profile). A site behind Cloudflare gets a
/// visible Edge window where the user ticks the check; the site is remembered and opens that way next time. Each page is
/// read once it has finished loading and stopped changing. Chapters are kept on disk as they come, so a cancelled or
/// failed run picks up where it stopped. One run at a time: Helm's own look for new chapters waits its turn.
/// </summary>
public sealed partial class NovelScraperService
{
    private static readonly int[] ProbePorts = [9222, 9223, 9224, 9225, 9229, 9230];
    private static readonly TimeSpan PageLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);
    private const int Attempts = 3;
    private const int MaxListPages = 300;
    private const int MaxChapterPages = 20;

    /// <summary>Locked chapters in a row after which the rest are taken to be locked too (a site's paid part).</summary>
    private const int LockedRun = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger<NovelScraperService> _logger;
    private readonly string _profile;
    private readonly string _cache;
    private readonly string _windowSites;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly ISettingsStore<NovelReaderSettings> _settings;

    public NovelScraperService(ISettingsStoreFactory settings, ILogger<NovelScraperService> logger)
    {
        _settings = settings.Get<NovelReaderSettings>(NovelReaderIds.ModuleId);
        _logger = logger;
        var root = Path.Combine(settings.Paths.Root, "cache", NovelReaderIds.ModuleId);
        _profile = Path.Combine(root, "scraper-profile");
        _cache = Path.Combine(root, "scraper");
        _windowSites = Path.Combine(root, "scraper-window-sites.txt");
    }

    /// <summary>A Cloudflare check came up while Helm looked by itself, with no one to pass it.</summary>
    private sealed class CheckNeededException() : Exception("The site asks for a check");

    /// <summary>Returns the CDP port of a browser already listening on this machine, or null.</summary>
    public async Task<int?> DetectBrowserPortAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        foreach (var port in ProbePorts)
        {
            if (ct.IsCancellationRequested) return null;
            try
            {
                var text = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct);
                if (text.Contains("webSocketDebuggerUrl")) return port;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// Scrapes the chapters listed at <paramref name="url"/>, in a new tab of the browser on <paramref name="existingPort"/>
    /// when given, otherwise in Helm's own Edge. With <paramref name="have"/> (a novel's chapters, continuing its
    /// download) only the chapters it lacks are fetched; the others come back as <see cref="ScrapeOutcome.Skipped"/>.
    /// </summary>
    public async Task<ScrapeResult> ScrapeAsync(string url, int? existingPort, IReadOnlyList<Chapter>? have,
        IProgress<(string Status, int Done, int Total)> progress, CancellationToken ct, Action<ScrapeResult>? early = null)
    {
        var start = Start(url);
        if (!_busy.Wait(0))
        {
            progress.Report(("Waiting for Helm's other Edge to finish (a look for new chapters, or a login window still open)…", 0, 0));
            await _busy.WaitAsync(ct);
        }
        try
        {
            return await RunAsync(start, existingPort, have, progress, early, quiet: false, ct);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// A scrape Helm starts by itself (new chapters to save, a phone's request): in a hidden Edge, nothing shown. Null when
    /// it cannot run now (another scrape or a login window is open) or the site asks for a check; tried again later.
    /// </summary>
    public async Task<ScrapeResult?> ScrapeQuietlyAsync(string url, IReadOnlyList<Chapter> have, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return null;
        try
        {
            return await RunAsync(Start(url), null, have, new Progress<(string, int, int)>(), null, quiet: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Could not download new chapters at {Url}: {Reason}", url, ex.Message);
            return null;
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<ScrapeResult> RunAsync(Uri start, int? existingPort, IReadOnlyList<Chapter>? have,
        IProgress<(string Status, int Done, int Total)> progress, Action<ScrapeResult>? early, bool quiet, CancellationToken ct)
    {
        TrimCache();
        progress.Report(("Opening the browser…", 0, 0));
        await using var browser = existingPort is { } port
            ? await ScraperBrowser.AttachAsync(port, ct)
            : await ScraperBrowser.LaunchAsync(BrowserPath(), _profile, visible: !quiet && OpensInWindow(start.Host), ct, quiet);
        _logger.LogInformation("Scraper started for {Url} ({Mode})", start, existingPort is not null ? "open browser" : quiet ? "quiet" : "own Edge");
        var list = await ReadChapterListAsync(browser, start, quiet, progress, ct);
        var cover = await DownloadCoverAsync(browser.Tab, list, ct);
        // A Traditional Chinese novel comes out in Simplified (NovelReaderSettings.ConvertTraditional).
        var simplify = _settings.Current.ConvertTraditional;
        Action<ScrapeResult>? first = early is null ? null : r => early(simplify ? ScrapedBook.Simplified(r) : r);
        var result = await ReadChaptersAsync(browser, list, have, progress, first, ct);
        if (simplify) result = ScrapedBook.Simplified(result);
        return result with { Cover = cover };
    }

    /// <summary>
    /// Looks at a novel's chapter list for chapters it lacks, in a hidden Edge, with nothing shown: the number of them, or
    /// null when it could not be told (a scrape is running, the site asks for a check, the page did not open).
    /// </summary>
    public async Task<int?> CountNewAsync(string url, IReadOnlyList<Chapter> have, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return null;
        try
        {
            var start = Start(url);
            var progress = new Progress<(string, int, int)>();
            await using var browser = await ScraperBrowser.LaunchAsync(BrowserPath(), _profile, visible: false, ct);
            var list = await ReadChapterListAsync(browser, start, quiet: true, progress, ct);
            return BookUpdate.Missing(have, list.Chapters).Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Could not look for new chapters at {Url}: {Reason}", url, ex.Message);
            return null;
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>A site searched by name: its address, and its search page with {0} for the name, or null to fill its search box.</summary>
    private sealed record SearchSite(string Name, string Home, string? Search);

    private static readonly SearchSite[] SearchSites =
    [
        new("ixdzs8", "https://ixdzs8.com/", "https://ixdzs8.com/bsearch?q={0}"),
        new("Qidian", "https://www.qidian.com/", "https://www.qidian.com/so/{0}.html"),
        new("piaotia", "https://www.piaotia.com/", null),
        new("xbanxia", "https://www.xbanxia.cc/", null),
        new("biquge.tw", "https://www.biquge.tw/", null),
    ];

    /// <summary>
    /// Looks for a novel by name on the sites Helm knows, all at once in a hidden Edge (a site that asks for a check or
    /// does not answer is left out). A site that opens the novel itself for an exact name gives that page.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, IProgress<(string Status, int Done, int Total)> progress, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        if (!_busy.Wait(0))
        {
            progress.Report(("Waiting for Helm's other Edge to finish…", 0, 0));
            await _busy.WaitAsync(ct);
        }
        try
        {
            progress.Report(($"Looking for “{query}” on {SearchSites.Length} sites…", 0, SearchSites.Length));
            await using var browser = await ScraperBrowser.LaunchAsync(BrowserPath(), _profile, visible: false, ct, quiet: true);
            var tabs = new List<CdpSession> { browser.Tab };
            for (var i = 1; i < SearchSites.Length; i++)
                if (await browser.OpenTabAsync(ct) is { } tab) tabs.Add(tab);
            var done = 0;
            // More sites than tabs (a tab would not open): a tab takes its sites one after another.
            var turns = tabs.Select(_ => new SemaphoreSlim(1, 1)).ToList();
            var found = await Task.WhenAll(SearchSites.Select(async (site, i) =>
            {
                var slot = i % tabs.Count;
                await turns[slot].WaitAsync(ct);
                try
                {
                    var hits = await SearchSiteAsync(browser, tabs[slot], site, query, ct);
                    progress.Report(($"Looking for “{query}”…", Interlocked.Increment(ref done), SearchSites.Length));
                    return hits;
                }
                finally
                {
                    turns[slot].Release();
                }
            }));
            return [.. found.SelectMany(h => h)];
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<IReadOnlyList<SearchHit>> SearchSiteAsync(ScraperBrowser browser, CdpSession tab, SearchSite site, string query, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var token = timeout.Token;
            if (site.Search is { } search)
            {
                await LoadAsync(browser, tab, string.Format(System.Globalization.CultureInfo.InvariantCulture, search, Uri.EscapeDataString(query)), quiet: true,
                    new Progress<(string, int, int)>(), token);
            }
            else
            {
                await LoadAsync(browser, tab, site.Home, quiet: true, new Progress<(string, int, int)>(), token);
                var answer = await tab.EvaluateStringAsync(SearchFormJs + "(" + JsonSerializer.Serialize(query) + ")", token);
                if (answer != "submitted") return [];
                await Task.Delay(1500, token);
                await WaitAsync(browser, tab, token);
            }
            var state = await tab.EvaluateAsync<PageState>(StateJs, token) ?? new PageState();
            var list = await ReadListAsync(tab, token);
            var hits = NovelSearch.Hits(query, state.Href, list.Links, site.Name).ToList();
            // An exact name opens the novel itself on some sites (piaotia): that page is the result.
            if (hits.Count == 0 && NovelSearch.IsBookPage(state.Href)
                && ScrapedBook.Title(list.BookName, list.Heading, list.Title) is { } title
                && BookUpdate.Key(Helm.Modules.NovelReader.Text.TraditionalChinese.ToSimplified(title))
                    .Contains(BookUpdate.Key(Helm.Modules.NovelReader.Text.TraditionalChinese.ToSimplified(query)), StringComparison.Ordinal))
                hits.Add(new SearchHit(title, state.Href, site.Name));
            _logger.LogInformation("Search for {Query} on {Site}: {Count} found", query, site.Name, hits.Count);
            return hits;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Search on {Site} gave nothing: {Reason}", site.Name, ex.Message);
            return [];
        }
    }

    /// <summary>Fills a site's own search box and submits it in the same tab (the browser encodes the name as the site wants, GBK too).</summary>
    private const string SearchFormJs = """
        (function(q) {
          var box = document.querySelector('input[name="searchkey"],input[name="keyword"],input[name="q"],input[name="wd"],input[name="s"],input[type="search"],input[name*="key"]');
          if (!box || !box.form) return 'no box';
          box.value = q;
          box.dispatchEvent(new Event('input', { bubbles: true }));
          var form = box.form;
          form.target = '_self';
          var button = form.querySelector('[type="submit"],button');
          if (button) button.click(); else form.submit();
          return 'submitted';
        })
        """;

    /// <summary>
    /// Opens <paramref name="url"/> in a normal Edge window on Helm's own scraping profile, for the user to log in to the
    /// site (paid chapters, Fanqie after chapter 10): the login stays in that profile, so every later scrape, hidden or
    /// not, is logged in. Scraping waits until the window is closed (one Edge per profile). Returns once it is closed.
    /// </summary>
    public async Task LogInAsync(string url, IProgress<(string Status, int Done, int Total)> progress, CancellationToken ct)
    {
        var start = Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri
            : throw new InvalidOperationException("Paste the web address of the site to log in to (https://…).");
        if (!_busy.Wait(0))
        {
            progress.Report(("Waiting for the scrape that is running to finish…", 0, 0));
            await _busy.WaitAsync(ct);
        }
        try
        {
            Directory.CreateDirectory(_profile);
            var info = new System.Diagnostics.ProcessStartInfo(BrowserPath()) { UseShellExecute = false };
            foreach (var arg in new[] { $"--user-data-dir={_profile}", "--no-first-run", "--no-default-browser-check", "--new-window", start.AbsoluteUri })
                info.ArgumentList.Add(arg);
            using var edge = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Edge did not start.");
            progress.Report(("Log in to the site in the Edge window, then close the window. Scraping waits until it is closed.", 0, 0));
            _logger.LogInformation("Login window opened for {Host}", start.Host);
            await edge.WaitForExitAsync(ct);
            // Edge hands the window to a process already on the profile and the one started exits at once: wait for that one too.
            while (File.Exists(Path.Combine(_profile, "lockfile")) && IsLocked(Path.Combine(_profile, "lockfile")))
                await Task.Delay(1000, ct);
            _logger.LogInformation("Login window closed for {Host}", start.Host);
        }
        finally
        {
            _busy.Release();
        }
    }

    private static bool IsLocked(string file)
    {
        try
        {
            using var _ = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The cover picture the chapter list shows, as the site sends it (made small later, on the UI thread), or null. Asked
    /// for like the page does (its address as the referrer, the browser's user agent), as image servers often want.
    /// </summary>
    private async Task<byte[]?> DownloadCoverAsync(CdpSession tab, ChapterList list, CancellationToken ct)
    {
        if (list.CoverUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        try
        {
            var agent = (await tab.EvaluateStringAsync("navigator.userAgent", ct)).Replace("HeadlessChrome", "Chrome", StringComparison.Ordinal);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Referrer = new Uri(list.TocUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", agent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true
                || response.Content.Headers.ContentLength > MaxCoverBytes)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length is > 0 and <= MaxCoverBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogInformation("No cover from {Url}: {Reason}", url, ex.Message);
            return null;
        }
    }

    private const int MaxCoverBytes = 5 * 1024 * 1024;

    private static Uri Start(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Paste the web address of the novel's chapter list (https://…).");
        return start;
    }

    private sealed record ChapterList(string TocUrl, string Book, string? Site, IReadOnlyList<WebLink> Chapters, string? CoverUrl);

    /// <summary>The chapter list, with every further page of it.</summary>
    private async Task<ChapterList> ReadChapterListAsync(ScraperBrowser browser, Uri start, bool quiet,
        IProgress<(string Status, int Done, int Total)> progress, CancellationToken ct)
    {
        progress.Report(("Reading the chapter list…", 0, 0));
        var first = await LoadAsync(browser, browser.Tab, start.AbsoluteUri, quiet, progress, ct);
        var list = await ReadListAsync(browser.Tab, ct);
        // A book's information page (a search result) shows its latest chapters only: its chapter list is a link away.
        if (ChapterLinks.Pick(first.Href, list.Links).Count < 30 && ChapterLinks.CatalogLink(first.Href, list.Links) is { } catalog)
        {
            _logger.LogInformation("Scraper follows the chapter list link {Url}", catalog);
            var cover = list.Cover;
            first = await LoadAsync(browser, browser.Tab, catalog, quiet, progress, ct);
            list = await ReadListAsync(browser.Tab, ct);
            if (string.IsNullOrEmpty(list.Cover)) list = list with { Cover = cover };
        }
        var tocUrl = first.Href;
        var links = new List<WebLink>(list.Links);
        var visited = new HashSet<string>(StringComparer.Ordinal) { start.AbsoluteUri, tocUrl };
        var pending = new Queue<string>(ChapterLinks.ListPages(tocUrl, list.Links));
        while (pending.TryDequeue(out var page) && visited.Count < MaxListPages)
        {
            if (!visited.Add(page)) continue;
            progress.Report(($"Reading the chapter list, page {visited.Count - 1}…", 0, 0));
            try
            {
                var state = await LoadAsync(browser, browser.Tab, page, quiet, progress, ct);
                var more = await ReadListAsync(browser.Tab, ct);
                links.AddRange(more.Links);
                foreach (var next in ChapterLinks.ListPages(state.Href, more.Links)) pending.Enqueue(next);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or CheckNeededException))
            {
                _logger.LogWarning(ex, "Scraper could not read list page {Url}", page);
            }
        }

        var chapters = ChapterLinks.Pick(tocUrl, links);
        if (chapters.Count == 0)
            throw new InvalidOperationException("No chapters on this page. Open the novel's chapter list (目录) and paste that address.");
        var book = ScrapedBook.Title(list.BookName, list.Heading, list.Title);
        _logger.LogInformation("Scraper found {Count} chapters of {Book} at {Url}", chapters.Count, book, tocUrl);
        return new ChapterList(tocUrl, book, ScrapedBook.SiteName(list.Title), chapters, string.IsNullOrEmpty(list.Cover) ? null : list.Cover);
    }

    /// <summary>
    /// Every chapter of the list in order: fetched (or kept from an earlier run), or skipped when the novel has it.
    /// Chapters are read in several tabs at once (<see cref="NovelReaderSettings.ScrapeTabs"/>), in list order; the
    /// first tab alone deals with a Cloudflare check, so when another tab meets one, or the site says it is too busy,
    /// the extra tabs stop and the first goes on with their chapters. Once the first <see cref="EarlyChapters"/> wanted
    /// chapters are in, <paramref name="early"/> gets them, so reading can start while the rest download.
    /// </summary>
    private async Task<ScrapeResult> ReadChaptersAsync(ScraperBrowser browser, ChapterList list, IReadOnlyList<Chapter>? have,
        IProgress<(string Status, int Done, int Total)> progress, Action<ScrapeResult>? early, CancellationToken ct)
    {
        var chapters = list.Chapters;
        var wanted = have is null ? null : BookUpdate.Missing(have, chapters).Select(l => l.Url).ToHashSet(StringComparer.Ordinal);
        var cache = Path.Combine(_cache, Hash(list.TocUrl));
        Directory.CreateDirectory(cache);
        Directory.SetLastWriteTimeUtc(cache, DateTime.UtcNow);
        var chapterUrls = chapters.Select(c => c.Url).ToHashSet(StringComparer.Ordinal);
        var slots = new ScrapeItem?[chapters.Count];
        var todo = new List<int>();
        int done = 0, saved = 0, lockedRun = 0;

        for (var i = 0; i < chapters.Count; i++)
        {
            var link = chapters[i];
            if (wanted is not null && !wanted.Contains(link.Url)) slots[i] = new ScrapeItem(link, ScrapeOutcome.Skipped, null);
            else if (ReadSaved(Path.Combine(cache, Hash(link.Url) + ".txt")) is { } kept)
            {
                slots[i] = new ScrapeItem(link, ScrapeOutcome.Got, kept);
                saved++;
                done++;
            }
            else todo.Add(i);
        }
        var total = done + todo.Count;
        var resumed = saved > 0 ? $" ({saved} kept from the last run)" : "";

        // Wanted chapters, in list order: the first ones decide when reading can start.
        var order = Enumerable.Range(0, chapters.Count).Where(i => slots[i]?.Outcome != ScrapeOutcome.Skipped).ToList();
        var earlySent = early is null || order.Count <= EarlyChapters;
        void MaybeEarly()
        {
            if (earlySent) return;
            var first = order.Take(EarlyChapters).ToList();
            if (first.Any(i => slots[i] is null)) return;
            earlySent = true;
            early!(new ScrapeResult(list.Book, list.TocUrl, [.. first.Select(i => slots[i]!)]));
        }
        MaybeEarly();

        var queue = new Queue<int>(todo);
        var alone = new List<int>();
        var gate = new object();
        var extraStopped = false;

        async Task WorkAsync(CdpSession tab, bool main)
        {
            while (true)
            {
                int i;
                lock (gate)
                {
                    if (!main && extraStopped) return;
                    if (!queue.TryDequeue(out i)) return;
                }
                var link = chapters[i];
                if (Volatile.Read(ref lockedRun) >= LockedRun)
                {
                    lock (gate)
                    {
                        slots[i] = new ScrapeItem(link with { Text = Name(link, null, i) }, ScrapeOutcome.Locked, null);
                        done++;
                    }
                    continue;
                }
                progress.Report(($"{Volatile.Read(ref done)}/{total} chapters · now {Name(link, null, i)}{resumed}", Volatile.Read(ref done), total));
                ScrapeItem item;
                try
                {
                    item = await FetchAsync(browser, tab, main, link, i, list, chapterUrls, cache, progress, ct);
                }
                catch (TabStoppedException)
                {
                    // Left for the first tab: a check to pass, a busy site, or this tab closed with the window.
                    lock (gate)
                    {
                        extraStopped = true;
                        alone.Add(i);
                    }
                    return;
                }
                lock (gate)
                {
                    slots[i] = item;
                    done++;
                    lockedRun = item.Outcome == ScrapeOutcome.Locked ? lockedRun + 1 : 0;
                    MaybeEarly();
                }
            }
        }

        var tabs = Math.Clamp(_settings.Current.ScrapeTabs, 1, 5);
        var workers = new List<Task> { WorkAsync(browser.Tab, main: true) };
        for (var t = 1; t < Math.Min(tabs, todo.Count); t++)
            if (await browser.OpenTabAsync(ct) is { } extra) workers.Add(WorkAsync(extra, main: false));
        await Task.WhenAll(workers);
        // What the extra tabs left goes through the first one, in order.
        foreach (var i in alone.Order())
        {
            lock (gate) queue.Enqueue(i);
        }
        if (alone.Count > 0)
        {
            _logger.LogInformation("Scraper goes on with one tab for {Count} chapters", alone.Count);
            await WorkAsync(browser.Tab, main: true);
        }

        var items = slots.Select((s, i) => s ?? new ScrapeItem(chapters[i], ScrapeOutcome.Failed, null)).ToList();
        var result = new ScrapeResult(list.Book, list.TocUrl, items);
        var got = result.Count(ScrapeOutcome.Got);
        var locked = result.Count(ScrapeOutcome.Locked);
        if (got == 0 && have is null)
            throw new InvalidOperationException(locked > 0
                ? "Every chapter is locked: the site wants you to log in or pay. Log in to the site (the web menu on a novel, or your own browser) and try again."
                : "No chapter text came. The site may need a login, or show its text in a way Helm cannot read.");

        _logger.LogInformation("Scraper finished {Book}: {Got}/{Total} chapters, {Locked} locked, {Skipped} already there, {Tabs} tabs",
            list.Book, got, total, locked, result.Count(ScrapeOutcome.Skipped), workers.Count);
        return result;
    }

    /// <summary>Wanted chapters that must be in before reading can start while the rest download.</summary>
    public const int EarlyChapters = 20;

    /// <summary>An extra tab gives its chapter back to the first tab (a check, a busy site, a closed window).</summary>
    private sealed class TabStoppedException() : Exception("The tab stopped");

    /// <summary>A site's answer when asked too often.</summary>
    private sealed class TooBusyException() : Exception("The site is busy");

    /// <summary>One chapter, tried a few times; kept on disk when it came.</summary>
    private async Task<ScrapeItem> FetchAsync(ScraperBrowser browser, CdpSession tab, bool main, WebLink link, int index, ChapterList list,
        IReadOnlySet<string> chapterUrls, string cache, IProgress<(string, int, int)> progress, CancellationToken ct)
    {
        ChapterPage? read = null;
        for (var attempt = 1; attempt <= Attempts && read is null; attempt++)
        {
            try
            {
                read = await ReadChapterAsync(browser, tab, main, link, index, list.Book, list.Site, chapterUrls, progress, ct);
            }
            catch (CheckNeededException) when (main && browser.Quiet)
            {
                // No one to pass the check: the quiet run ends here and is tried again later.
                throw;
            }
            catch (Exception ex) when (!main && (ex is CheckNeededException or TooBusyException || !tab.IsOpen))
            {
                throw new TabStoppedException();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Scraper attempt {Attempt} failed for {Title} at {Url}", attempt, link.Text, link.Url);
                if (!(main ? browser.Tab : tab).IsOpen)
                    throw new InvalidOperationException("The browser window was closed. Scrape again to pick up where it stopped.", ex);
                if (attempt < Attempts) await Task.Delay(TimeSpan.FromSeconds((ex is TooBusyException ? 5 : 1.5) * attempt), ct);
            }
        }

        var named = link with { Text = read?.Chapter.Title ?? Name(link, null, index) };
        if (read is { Locked: true }) return new ScrapeItem(named, ScrapeOutcome.Locked, null);
        if (read is null || read.Chapter.Paragraphs.Count == 0)
        {
            _logger.LogWarning("Scraper got no text from {Title} at {Url}", link.Text, link.Url);
            return new ScrapeItem(named, ScrapeOutcome.Failed, null);
        }
        await File.WriteAllLinesAsync(Path.Combine(cache, Hash(link.Url) + ".txt"), read.Chapter.Paragraphs.Prepend(read.Chapter.Title), ct);
        return new ScrapeItem(named, ScrapeOutcome.Got, read.Chapter);
    }

    private sealed record ChapterPage(ScrapedChapter Chapter, bool Locked);

    /// <summary>One chapter, joined from all its pages (123.html, 123_2.html…).</summary>
    private async Task<ChapterPage> ReadChapterAsync(ScraperBrowser browser, CdpSession tab, bool main, WebLink link, int index, string book, string? site,
        IReadOnlySet<string> chapterUrls, IProgress<(string, int, int)> progress, CancellationToken ct)
    {
        var parts = new List<string>();
        // The first tab is replaced when Edge starts again as a window for a check.
        CdpSession Current() => main ? browser.Tab : tab;
        var seen = new HashSet<string>(StringComparer.Ordinal) { link.Url };
        string? heading = null, pageText = "";
        var pageUrl = link.Url;
        for (var page = 0; page < MaxChapterPages; page++)
        {
            var state = await LoadAsync(browser, Current(), pageUrl, quiet: false, progress, ct);
            var read = await Current().EvaluateAsync<ChapterJson>(ChapterJs, ct) ?? new ChapterJson();
            var text = read.Text ?? "";
            if (FanqieText.IsScrambled(text))
            {
                // Fanqie's own characters: decoded with the table made for its font, never with another one.
                if (!FanqieText.KnowsFont(read.Fonts ?? ""))
                    throw new InvalidOperationException("Fanqie changed the font it scrambles its text with; Helm cannot read it until it is updated.");
                text = FanqieText.Decode(text);
            }
            heading ??= string.IsNullOrWhiteSpace(read.Heading) ? null : FanqieText.Decode(read.Heading.Trim());
            parts.Add(text);
            pageText = read.Page ?? "";
            var next = ChapterLinks.Continuation(state.Href, link.Url, read.Next ?? [], chapterUrls, seen);
            if (next is null) break;
            seen.Add(next);
            pageUrl = next;
        }
        var title = Name(link, heading, index);
        var paragraphs = parts.SelectMany(p => ChapterText.Clean(p, book, site, title, heading)).ToList();
        return new ChapterPage(new ScrapedChapter(title, paragraphs), ChapterText.LooksLocked(paragraphs, pageText));
    }

    private static string Name(WebLink link, string? heading, int index) =>
        link.Text.Length > 0 && !link.Text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? link.Text
        : heading ?? $"第{index + 1}章";

    private static async Task<ListJson> ReadListAsync(CdpSession tab, CancellationToken ct)
    {
        var list = await tab.EvaluateAsync<ListJson>(ListJs, ct) ?? new ListJson();
        return list.Links is null ? list with { Links = [] } : list;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"429|Too Many Requests|503 Service|Service Unavailable|请求过于频繁|訪問過於頻繁|访问过于频繁|访问频率")]
    private static partial System.Text.RegularExpressions.Regex BusyPattern();

    /// <summary>
    /// Opens <paramref name="url"/> in <paramref name="tab"/> and waits until it has loaded and stopped changing. A
    /// Cloudflare check in the first tab brings up a window for the user to pass it (another tab gives up); a page that
    /// cannot be reached throws with the browser's reason, and a site that says it is busy throws so the caller waits.
    /// </summary>
    private async Task<PageState> LoadAsync(ScraperBrowser browser, CdpSession tab, string url, bool quiet, IProgress<(string, int, int)> progress, CancellationToken ct)
    {
        await tab.NavigateAsync(url, ct);
        var state = await WaitAsync(browser, tab, ct);
        if (state.Challenge && (quiet || browser.Quiet || tab != browser.Tab)) throw new CheckNeededException();
        if (state.Challenge) state = await PassCheckAsync(browser, url, progress, ct);
        if (state.Href.StartsWith("chrome-error:", StringComparison.Ordinal))
            throw new InvalidOperationException("The page could not be opened: " + FirstLine(state.Body));
        if (state.Text < 400 && BusyPattern().IsMatch(state.Title + " " + state.Body)) throw new TooBusyException();
        return state;
    }

    private static async Task<PageState> WaitAsync(ScraperBrowser browser, CdpSession tab, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        // Cloudflare lets a real window through by itself after a few seconds; a hidden one never.
        var challengeWait = TimeSpan.FromSeconds(browser.Visible ? 8 : 2);
        PageState? previous = null;
        while (true)
        {
            await Task.Delay(previous is null ? 250 : 400, ct);
            var state = await tab.EvaluateAsync<PageState>(StateJs, ct) ?? new PageState();
            var waited = DateTime.UtcNow - started;
            if (state.Href.StartsWith("chrome-error:", StringComparison.Ordinal)) return state;
            if (state.Challenge && waited < challengeWait)
            {
                previous = null;
                continue;
            }
            if (state.Challenge) return state;
            if (state.Ready == "complete" && previous is { } p && p.Links == state.Links && p.Text == state.Text && p.Href == state.Href) return state;
            if (waited > PageLimit) return state;
            previous = state;
        }
    }

    private async Task<PageState> PassCheckAsync(ScraperBrowser browser, string url, IProgress<(string, int, int)> progress, CancellationToken ct)
    {
        var host = new Uri(url).Host;
        _logger.LogInformation("Scraper hit a Cloudflare check at {Host}", host);
        progress.Report((browser.Attached
            ? "Cloudflare is checking: confirm it in the browser tab Helm opened. Helm goes on by itself afterwards."
            : "Cloudflare is checking: tick the box in the Edge window that opened. Helm goes on by itself afterwards.", 0, 0));
        await browser.ShowAsync(url, ct);
        RememberWindow(host);
        var until = DateTime.UtcNow + CheckLimit;
        while (DateTime.UtcNow < until)
        {
            var state = await WaitAsync(browser, browser.Tab, ct);
            if (!state.Challenge)
            {
                _logger.LogInformation("Scraper passed the check at {Host}", host);
                await browser.MinimizeAsync(ct);
                return state;
            }
            await Task.Delay(1000, ct);
        }
        throw new InvalidOperationException("The Cloudflare check was not passed within 5 minutes.");
    }

    private bool OpensInWindow(string host)
    {
        try
        {
            return File.Exists(_windowSites) && File.ReadAllLines(_windowSites).Contains(host, StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void RememberWindow(string host)
    {
        if (OpensInWindow(host)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_windowSites)!);
            File.AppendAllLines(_windowSites, [host]);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not remember {Host}", host);
        }
    }

    private static ScrapedChapter? ReadSaved(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var lines = File.ReadAllLines(file);
            return lines.Length > 1 ? new ScrapedChapter(lines[0], lines[1..]) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the chapters kept from scraping <paramref name="sourceUrl"/>: the novel was saved. Until then they stay, so
    /// "Retry failed", or a preview closed and scraped again, does not download them again.
    /// </summary>
    public void ForgetChapters(string sourceUrl) => TryDelete(Path.Combine(_cache, Hash(sourceUrl)));

    /// <summary>Deletes chapters kept from runs more than a week old (scraped and never saved).</summary>
    private void TrimCache()
    {
        if (!Directory.Exists(_cache)) return;
        foreach (var folder in Directory.EnumerateDirectories(_cache))
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > CacheAge) TryDelete(folder);
    }

    private void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Folder}", folder);
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no answer";

    private static string BrowserPath() => EdgePath() ?? ChromePath()
        ?? throw new InvalidOperationException("Microsoft Edge or Chrome is needed to scrape. Install one and try again.");

    /// <summary>Where Edge is installed (App Paths in the registry, or the usual folders).</summary>
    private static string? EdgePath()
    {
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
                if (key?.GetValue(null) is string path && File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
            }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var path = Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? ChromePath()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            var path = Path.Combine(Environment.GetFolderPath(folder), "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // ── What the page tells ─────────────────────────────────────────────────────

    private sealed record PageState
    {
        public string Ready { get; init; } = "";
        public string Href { get; init; } = "";
        public int Links { get; init; }
        public int Text { get; init; }
        public bool Challenge { get; init; }
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
    }

    private sealed record ListJson
    {
        public string? BookName { get; init; }
        public string? Heading { get; init; }
        public string? Title { get; init; }
        public string? Cover { get; init; }
        public List<WebLink> Links { get; init; } = [];
    }

    private sealed record ChapterJson
    {
        public string? Heading { get; init; }
        public string? Text { get; init; }
        public string? Page { get; init; }
        public List<WebLink>? Next { get; init; }
        public string? Fonts { get; init; }
    }

    private const string StateJs = """
        (function() {
          var t = document.title || '';
          var challenge = /Just a moment|Checking your browser|Attention Required|请稍候|請稍候|安全验证/i.test(t)
            || !!document.querySelector('#challenge-form,#challenge-stage,#cf-challenge-running,.cf-turnstile,[name="cf-turnstile-response"]');
          var body = document.body ? document.body.innerText || '' : '';
          return JSON.stringify({ ready: document.readyState, href: location.href, title: t, links: document.links.length,
            text: body.length, challenge: challenge, body: body.slice(0, 300) });
        })()
        """;

    /// <summary>Every link (and page picker option) of the list page, with the names the page gives the book.</summary>
    private const string ListJs = """
        (function() {
          var meta = function(p) { var m = document.querySelector('meta[property="' + p + '"],meta[name="' + p + '"]'); return m ? m.content : ''; };
          var links = [];
          for (var a of document.querySelectorAll('a[href]')) links.push({ text: (a.innerText || a.textContent || '').trim(), url: a.href });
          for (var o of document.querySelectorAll('select option[value]')) {
            var v = o.value || '';
            if (/^(https?:)?\/|\.html?$|\?/.test(v)) { try { links.push({ text: (o.textContent || '').trim(), url: new URL(v, location.href).href }); } catch (e) {} }
          }
          var h1 = document.querySelector('h1');
          // The cover: the page's picture tag, else a picture in a cover box (biquge's #fmimg), else one named like a cover.
          var cover = meta('og:image');
          if (!cover) {
            var img = document.querySelector('#fmimg img,.book-img img,.bookimg img,.book-cover img,.cover img,[class*="cover"] img,img[class*="cover"],img[src*="cover"]');
            if (img) cover = img.currentSrc || img.src;
          }
          try { cover = cover ? new URL(cover, location.href).href : ''; } catch (e) { cover = ''; }
          return JSON.stringify({ bookName: meta('og:novel:book_name') || meta('og:novel:title'), heading: h1 ? h1.innerText.trim() : '',
            title: document.title, cover: cover, links: links });
        })()
        """;

    /// <summary>
    /// The chapter's text: the known content boxes of novel sites first, else the box holding the most Chinese text of
    /// its own (text and paragraphs directly inside it). Read with innerText on the page itself so line breaks stay.
    /// </summary>
    private const string ChapterJs = """
        (function() {
          var cjk = function(s) { var m = (s || '').match(/[㐀-鿿]/g); return m ? m.length : 0; };
          var known = ['#chaptercontent','#content','#booktxt','#txt','#htmlContent','#TextContent','#nr1','#nr','#chapter-content',
            '.chapter-content','.read-content','.readcontent','.txtnav','#acontent','.content','article','main'];
          var best = null;
          for (var sel of known) {
            var el = document.querySelector(sel);
            if (el && cjk(el.innerText) >= 200) { best = el; break; }
          }
          if (!best) {
            var top = 0;
            for (var el of document.querySelectorAll('div,article,section,main,td')) {
              var own = 0;
              for (var n of el.childNodes) {
                if (n.nodeType === 3) own += cjk(n.textContent);
                else if (n.nodeType === 1 && /^(P|SPAN|FONT)$/.test(n.tagName)) own += cjk(n.textContent);
              }
              if (own > top) { top = own; best = el; }
            }
          }
          if (best) for (var x of best.querySelectorAll('script,style,noscript,iframe,ins,.adsbygoogle')) x.style.display = 'none';
          var next = [];
          for (var a of document.querySelectorAll('a[href]')) {
            var t = (a.innerText || a.textContent || '').trim();
            if (/下一页|下一頁|下页|下頁/.test(t)) next.push({ text: t, url: a.href });
          }
          var h1 = document.querySelector('h1');
          var page = document.body ? document.body.innerText || '' : '';
          // The fonts the page names (Fanqie draws its common characters with one of its own).
          var fonts = Array.from(document.querySelectorAll('style')).map(function(s) { return (s.textContent || '').match(/[^\s'"()]+\.(woff2?|otf|ttf)/g) || []; }).flat().join(' ');
          return JSON.stringify({ heading: h1 ? h1.innerText.trim() : '', text: best ? best.innerText || '' : '',
            page: page.slice(0, 2000), next: next, fonts: fonts });
        })()
        """;

    /// <summary>Reads a JSON string a page script returned.</summary>
    internal static T? Parse<T>(string json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
