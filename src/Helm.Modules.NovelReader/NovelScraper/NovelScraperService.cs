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
public sealed class NovelScraperService
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

    public NovelScraperService(ISettingsStoreFactory settings, ILogger<NovelScraperService> logger)
    {
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
        IProgress<(string Status, int Done, int Total)> progress, CancellationToken ct)
    {
        var start = Start(url);
        if (!_busy.Wait(0))
        {
            progress.Report(("Waiting for Helm's own look for new chapters to finish…", 0, 0));
            await _busy.WaitAsync(ct);
        }
        try
        {
            TrimCache();
            progress.Report(("Opening the browser…", 0, 0));
            await using var browser = existingPort is { } port
                ? await ScraperBrowser.AttachAsync(port, ct)
                : await ScraperBrowser.LaunchAsync(BrowserPath(), _profile, visible: OpensInWindow(start.Host), ct);
            _logger.LogInformation("Scraper started for {Url} ({Mode})", url, existingPort is null ? "own Edge" : "open browser");
            var list = await ReadChapterListAsync(browser, start, quiet: false, progress, ct);
            return await ReadChaptersAsync(browser, list, have, progress, ct);
        }
        finally
        {
            _busy.Release();
        }
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

    private static Uri Start(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Paste the web address of the novel's chapter list (https://…).");
        if (start.Host.EndsWith("fanqienovel.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fanqie scrambles its text on the web, so it cannot be scraped. Read it with Novel Lens on the phone.");
        return start;
    }

    private sealed record ChapterList(string TocUrl, string Book, string? Site, IReadOnlyList<WebLink> Chapters);

    /// <summary>The chapter list, with every further page of it.</summary>
    private async Task<ChapterList> ReadChapterListAsync(ScraperBrowser browser, Uri start, bool quiet,
        IProgress<(string Status, int Done, int Total)> progress, CancellationToken ct)
    {
        progress.Report(("Reading the chapter list…", 0, 0));
        var first = await LoadAsync(browser, start.AbsoluteUri, quiet, progress, ct);
        var list = await ReadListAsync(browser.Tab, ct);
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
                var state = await LoadAsync(browser, page, quiet, progress, ct);
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
        return new ChapterList(tocUrl, book, ScrapedBook.SiteName(list.Title), chapters);
    }

    /// <summary>Every chapter of the list in order: fetched (or kept from an earlier run), or skipped when the novel has it.</summary>
    private async Task<ScrapeResult> ReadChaptersAsync(ScraperBrowser browser, ChapterList list, IReadOnlyList<Chapter>? have,
        IProgress<(string Status, int Done, int Total)> progress, CancellationToken ct)
    {
        var chapters = list.Chapters;
        var wanted = have is null ? null : BookUpdate.Missing(have, chapters).Select(l => l.Url).ToHashSet(StringComparer.Ordinal);
        var total = wanted?.Count ?? chapters.Count;
        var cache = Path.Combine(_cache, Hash(list.TocUrl));
        Directory.CreateDirectory(cache);
        Directory.SetLastWriteTimeUtc(cache, DateTime.UtcNow);
        var chapterUrls = chapters.Select(c => c.Url).ToHashSet(StringComparer.Ordinal);
        var items = new List<ScrapeItem>(chapters.Count);
        int done = 0, lockedRun = 0, saved = 0;

        for (var i = 0; i < chapters.Count; i++)
        {
            var link = chapters[i];
            if (wanted is not null && !wanted.Contains(link.Url))
            {
                items.Add(new ScrapeItem(link, ScrapeOutcome.Skipped, null));
                continue;
            }
            var file = Path.Combine(cache, Hash(link.Url) + ".txt");
            if (ReadSaved(file) is { } kept)
            {
                items.Add(new ScrapeItem(link, ScrapeOutcome.Got, kept));
                saved++;
                done++;
                continue;
            }
            if (lockedRun >= LockedRun)
            {
                items.Add(new ScrapeItem(link with { Text = Name(link, null, i) }, ScrapeOutcome.Locked, null));
                done++;
                continue;
            }

            var resumed = saved > 0 ? $" ({saved} kept from the last run)" : "";
            progress.Report(($"Chapter {done + 1}/{total}: {Name(link, null, i)}{resumed}", done, total));
            ChapterPage? read = null;
            for (var attempt = 1; attempt <= Attempts && read is null; attempt++)
            {
                try
                {
                    read = await ReadChapterAsync(browser, link, i, list.Book, list.Site, chapterUrls, progress, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Scraper attempt {Attempt} failed for {Title} at {Url}", attempt, link.Text, link.Url);
                    if (!browser.Tab.IsOpen)
                        throw new InvalidOperationException("The browser window was closed. Scrape again to pick up where it stopped.", ex);
                    if (attempt < Attempts) await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct);
                }
            }
            done++;

            var named = link with { Text = read?.Chapter.Title ?? Name(link, null, i) };
            if (read is { Locked: true })
            {
                lockedRun++;
                items.Add(new ScrapeItem(named, ScrapeOutcome.Locked, null));
                continue;
            }
            lockedRun = 0;
            if (read is null || read.Chapter.Paragraphs.Count == 0)
            {
                _logger.LogWarning("Scraper got no text from {Title} at {Url}", link.Text, link.Url);
                items.Add(new ScrapeItem(named, ScrapeOutcome.Failed, null));
                continue;
            }
            items.Add(new ScrapeItem(named, ScrapeOutcome.Got, read.Chapter));
            await File.WriteAllLinesAsync(file, read.Chapter.Paragraphs.Prepend(read.Chapter.Title), ct);
        }

        var result = new ScrapeResult(list.Book, list.TocUrl, items);
        var got = result.Count(ScrapeOutcome.Got);
        var locked = result.Count(ScrapeOutcome.Locked);
        if (got == 0 && have is null)
            throw new InvalidOperationException(locked > 0
                ? "Every chapter is locked: the site wants you to log in or pay. Log in in your browser, open it with remote debugging and try again."
                : "No chapter text came. The site may need a login, or show its text in a way Helm cannot read.");

        _logger.LogInformation("Scraper finished {Book}: {Got}/{Total} chapters, {Locked} locked, {Skipped} already there",
            list.Book, got, total, locked, result.Count(ScrapeOutcome.Skipped));
        return result;
    }

    private sealed record ChapterPage(ScrapedChapter Chapter, bool Locked);

    /// <summary>One chapter, joined from all its pages (123.html, 123_2.html…).</summary>
    private async Task<ChapterPage> ReadChapterAsync(ScraperBrowser browser, WebLink link, int index, string book, string? site,
        IReadOnlySet<string> chapterUrls, IProgress<(string, int, int)> progress, CancellationToken ct)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { link.Url };
        string? heading = null, pageText = "";
        var pageUrl = link.Url;
        for (var page = 0; page < MaxChapterPages; page++)
        {
            var state = await LoadAsync(browser, pageUrl, quiet: false, progress, ct);
            var read = await browser.Tab.EvaluateAsync<ChapterJson>(ChapterJs, ct) ?? new ChapterJson();
            heading ??= string.IsNullOrWhiteSpace(read.Heading) ? null : read.Heading.Trim();
            parts.Add(read.Text ?? "");
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

    /// <summary>
    /// Opens <paramref name="url"/> and waits until it has loaded and stopped changing. A Cloudflare check brings up a
    /// window for the user to pass it; a page that cannot be reached throws with the browser's reason.
    /// </summary>
    private async Task<PageState> LoadAsync(ScraperBrowser browser, string url, bool quiet, IProgress<(string, int, int)> progress, CancellationToken ct)
    {
        await browser.Tab.NavigateAsync(url, ct);
        var state = await WaitAsync(browser, ct);
        if (state.Challenge && quiet) throw new CheckNeededException();
        if (state.Challenge) state = await PassCheckAsync(browser, url, progress, ct);
        if (state.Href.StartsWith("chrome-error:", StringComparison.Ordinal))
            throw new InvalidOperationException("The page could not be opened: " + FirstLine(state.Body));
        return state;
    }

    private static async Task<PageState> WaitAsync(ScraperBrowser browser, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        // Cloudflare lets a real window through by itself after a few seconds; a hidden one never.
        var challengeWait = TimeSpan.FromSeconds(browser.Visible ? 8 : 2);
        PageState? previous = null;
        while (true)
        {
            await Task.Delay(previous is null ? 250 : 400, ct);
            var state = await browser.Tab.EvaluateAsync<PageState>(StateJs, ct) ?? new PageState();
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
            var state = await WaitAsync(browser, ct);
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

    private static string BrowserPath() => EdgeAnchor.EdgePath() ?? ChromePath()
        ?? throw new InvalidOperationException("Microsoft Edge or Chrome is needed to scrape. Install one and try again.");

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
        public string Body { get; init; } = "";
    }

    private sealed record ListJson
    {
        public string? BookName { get; init; }
        public string? Heading { get; init; }
        public string? Title { get; init; }
        public List<WebLink> Links { get; init; } = [];
    }

    private sealed record ChapterJson
    {
        public string? Heading { get; init; }
        public string? Text { get; init; }
        public string? Page { get; init; }
        public List<WebLink>? Next { get; init; }
    }

    private const string StateJs = """
        (function() {
          var t = document.title || '';
          var challenge = /Just a moment|Checking your browser|Attention Required|请稍候|請稍候|安全验证/i.test(t)
            || !!document.querySelector('#challenge-form,#challenge-stage,#cf-challenge-running,.cf-turnstile,[name="cf-turnstile-response"]');
          var body = document.body ? document.body.innerText || '' : '';
          return JSON.stringify({ ready: document.readyState, href: location.href, links: document.links.length,
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
          return JSON.stringify({ bookName: meta('og:novel:book_name') || meta('og:novel:title'), heading: h1 ? h1.innerText.trim() : '',
            title: document.title, links: links });
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
          return JSON.stringify({ heading: h1 ? h1.innerText.trim() : '', text: best ? best.innerText || '' : '',
            page: page.slice(0, 2000), next: next });
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
