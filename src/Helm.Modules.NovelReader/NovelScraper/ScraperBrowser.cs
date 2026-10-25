using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace Helm.Modules.NovelReader;

/// <summary>
/// The browser the scraper reads pages in: a new tab of a browser the user opened with a DevTools port (closed again at
/// the end, the user's own tabs are never touched), or an Edge of Helm's own on its own profile, hidden until a site
/// wants a person to pass a check, then a normal window that is minimized once the check is passed.
/// </summary>
internal sealed class ScraperBrowser : IAsyncDisposable
{
    private int _port;
    private readonly string? _tabId;
    private readonly string? _path;
    private readonly string? _profile;
    private Process? _process;

    private ScraperBrowser(int port, CdpSession tab, string? tabId, Process? process, string? path, string? profile, bool visible)
    {
        _port = port;
        Tab = tab;
        _tabId = tabId;
        _process = process;
        _path = path;
        _profile = profile;
        Visible = visible;
    }

    public CdpSession Tab { get; private set; }

    /// <summary>A tab of the user's browser rather than Helm's own Edge.</summary>
    public bool Attached => _process is null && _path is null;

    /// <summary>The pages are in a window the user can see (or bring back).</summary>
    public bool Visible { get; private set; }

    /// <summary>Helm runs it by itself, with no one to show a window to: a check ends the run instead.</summary>
    public bool Quiet { get; private init; }

    /// <summary>Opens a new tab in the browser listening on <paramref name="port"/>.</summary>
    public static async Task<ScraperBrowser> AttachAsync(int port, CancellationToken ct)
    {
        var (id, socket) = await NewTargetAsync(port, ct);
        var tab = await CdpSession.ConnectAsync(socket, ct);
        return new ScraperBrowser(port, tab, id, null, null, null, visible: true);
    }

    /// <summary>Starts Helm's own Edge (or Chrome) on <paramref name="profile"/>, hidden unless <paramref name="visible"/>.</summary>
    public static async Task<ScraperBrowser> LaunchAsync(string path, string profile, bool visible, CancellationToken ct, bool quiet = false)
    {
        var (process, port, tab) = await StartAsync(path, profile, visible, ct);
        var browser = new ScraperBrowser(port, tab, null, process, path, profile, visible) { Quiet = quiet };
        if (visible) await browser.MinimizeAsync(ct);
        return browser;
    }

    /// <summary>
    /// Shows the page to the user: a hidden Edge is started again as a window on the same profile (so what the user
    /// passes is kept for the hidden runs too) and opens <paramref name="url"/>; a window or tab is brought to the front.
    /// </summary>
    public async Task ShowAsync(string url, CancellationToken ct)
    {
        if (!Visible && _path is not null && _profile is not null)
        {
            Tab.Dispose();
            Stop();
            var (process, port, tab) = await StartAsync(_path, _profile, visible: true, ct);
            _port = port;
            _process = process;
            Tab = tab;
            Visible = true;
            await Tab.NavigateAsync(url, ct);
            return;
        }
        await SetWindowAsync("normal", ct);
        await Tab.CallAsync("Page.bringToFront", null, ct);
    }

    /// <summary>Minimizes Helm's own window; a tab of the user's browser stays as it is.</summary>
    public Task MinimizeAsync(CancellationToken ct) => Attached || !Visible ? Task.CompletedTask : SetWindowAsync("minimized", ct);

    private async Task SetWindowAsync(string state, CancellationToken ct)
    {
        if (Attached) return;
        try
        {
            var window = await Tab.CallAsync("Browser.getWindowForTarget", null, ct);
            var id = window.GetProperty("windowId").GetInt32();
            await Tab.CallAsync("Browser.setWindowBounds", new { windowId = id, bounds = new { windowState = state } }, ct);
        }
        catch (InvalidOperationException)
        {
            // The window stays as it is; the pages are read all the same.
        }
    }

    private static async Task<(Process Process, int Port, CdpSession Tab)> StartAsync(string path, string profile, bool visible, CancellationToken ct)
    {
        Directory.CreateDirectory(profile);
        // An Edge left open on this profile (Helm closed mid-run) would take the new start over without its DevTools port.
        var lockFile = Path.Combine(profile, "lockfile");
        try
        {
            if (File.Exists(lockFile)) File.Delete(lockFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("An Edge window from an earlier scrape is still open. Close it and try again.");
        }

        var port = FreePort();
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string>
        {
            $"--remote-debugging-port={port}", "--remote-debugging-address=127.0.0.1", $"--user-data-dir={profile}",
            "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-component-update",
            // Sites look at navigator.webdriver to turn robots away.
            "--disable-blink-features=AutomationControlled",
        };
        if (visible) args.Add("--window-size=1100,850");
        else args.Add("--headless=new");
        args.Add("about:blank");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The browser did not start.");

        try
        {
            var tab = await CdpSession.ConnectAsync(await PageSocketAsync(port, process, ct), ct);
            if (!visible) await HideHeadlessAsync(tab, ct);
            return (process, port, tab);
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    private static async Task<string> PageSocketAsync(int port, Process process, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 75; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var list = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct));
                var socket = list.RootElement.EnumerateArray()
                    .Where(t => t.GetProperty("type").GetString() == "page")
                    .Select(t => t.GetProperty("webSocketDebuggerUrl").GetString())
                    .FirstOrDefault();
                if (socket is not null) return socket;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
            }
            if (process.HasExited && i > 10) break;
            await Task.Delay(200, ct);
        }
        throw new InvalidOperationException("The browser did not answer. Close any Edge window left from an earlier scrape and try again.");
    }

    private void Stop()
    {
        if (_process is null) return;
        Kill(_process);
        _process = null;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        process.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Another tab of the same browser (same cookies, so a check passed in the first tab holds), for reading chapters at
    /// the same time; null when the browser will not open one.
    /// </summary>
    public async Task<CdpSession?> OpenTabAsync(CancellationToken ct)
    {
        try
        {
            var (id, socket) = await NewTargetAsync(_port, ct);
            var tab = await CdpSession.ConnectAsync(socket, ct);
            if (!Visible && !Attached) await HideHeadlessAsync(tab, ct);
            lock (_extra) _extra.Add((id, tab));
            return tab;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
            or System.Net.WebSockets.WebSocketException or KeyNotFoundException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Tab.Dispose();
        List<(string Id, CdpSession Tab)> extra;
        lock (_extra) extra = [.. _extra];
        foreach (var (_, tab) in extra) tab.Dispose();
        // Helm's own Edge goes with its process; tabs opened in the user's browser are closed one by one.
        if (Attached)
            foreach (var id in extra.Select(e => e.Id).Prepend(_tabId).OfType<string>()) await CloseTargetAsync(_port, id);
        Stop();
    }

    private readonly List<(string Id, CdpSession Tab)> _extra = [];

    /// <summary>A new blank tab of the browser on <paramref name="port"/>: its id and DevTools address.</summary>
    private static async Task<(string Id, string Socket)> NewTargetAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var address = $"http://127.0.0.1:{port}/json/new?about:blank";
        // Chromium 111+ wants PUT here; older ones only answer GET.
        using var put = await http.PutAsync(address, null, ct);
        var json = put.IsSuccessStatusCode ? await put.Content.ReadAsStringAsync(ct) : await http.GetStringAsync(address, ct);
        using var target = JsonDocument.Parse(json);
        var id = target.RootElement.GetProperty("id").GetString() ?? "";
        var socket = target.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
            ?? throw new InvalidOperationException("The browser did not open a tab for Helm.");
        return (id, socket);
    }

    private static async Task CloseTargetAsync(int port, string id)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            await http.GetStringAsync($"http://127.0.0.1:{port}/json/close/{id}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    /// <summary>A hidden Edge says "HeadlessChrome" in its user agent, which some sites refuse.</summary>
    private static async Task HideHeadlessAsync(CdpSession tab, CancellationToken ct)
    {
        var agent = await tab.EvaluateStringAsync("navigator.userAgent", ct);
        await tab.CallAsync("Network.setUserAgentOverride", new { userAgent = agent.Replace("HeadlessChrome", "Chrome", StringComparison.Ordinal) }, ct);
    }
}

/// <summary>One page (tab) driven through the DevTools protocol.</summary>
internal sealed class CdpSession : IDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _calls = [];
    private int _nextId;

    private CdpSession(ClientWebSocket ws)
    {
        _ws = ws;
        _ = ReceiveLoopAsync();
    }

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public static async Task<CdpSession> ConnectAsync(string socketUrl, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await ws.ConnectAsync(new Uri(socketUrl), ct);
        var session = new CdpSession(ws);
        await session.CallAsync("Page.enable", null, ct);
        return session;
    }

    /// <summary>Starts loading <paramref name="url"/>; a page that cannot be reached at all throws with the browser's reason.</summary>
    public async Task NavigateAsync(string url, CancellationToken ct)
    {
        var result = await CallAsync("Page.navigate", new { url }, ct);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 } reason
            && reason != "net::ERR_ABORTED")
            throw new InvalidOperationException($"The page could not be opened ({reason}).");
    }

    public async Task<string> EvaluateStringAsync(string expression, CancellationToken ct)
    {
        var r = await CallAsync("Runtime.evaluate", new { expression, returnByValue = true }, ct);
        return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>Runs a script that returns JSON and reads it.</summary>
    public async Task<T?> EvaluateAsync<T>(string expression, CancellationToken ct) where T : class =>
        NovelScraperService.Parse<T>(await EvaluateStringAsync(expression, ct));

    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_calls) _calls[id] = tcs;
        try
        {
            if (_ws.State != WebSocketState.Open) throw new InvalidOperationException("The browser closed the page.");
            var msg = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
            await _send.WaitAsync(ct);
            try
            {
                await _ws.SendAsync(msg, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _send.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                return await tcs.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"The browser did not answer ({method}).");
            }
        }
        catch (WebSocketException ex)
        {
            throw new InvalidOperationException("The browser closed the page.", ex);
        }
        finally
        {
            lock (_calls) _calls.Remove(id);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                Handle(ms.ToArray());
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
        // Calls still waiting will get no answer: the window was closed or the browser quit.
        lock (_calls)
            foreach (var call in _calls.Values) call.TrySetException(new InvalidOperationException("The browser closed the page."));
    }

    private void Handle(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl)) return;
        TaskCompletionSource<JsonElement>? tcs;
        lock (_calls) _calls.TryGetValue(idEl.GetInt32(), out tcs);
        if (tcs is null) return;
        if (root.TryGetProperty("error", out var err))
            tcs.TrySetException(new InvalidOperationException("CDP: " + err.GetRawText()));
        else
            tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
    }

    public void Dispose()
    {
        _ws.Abort();
        _ws.Dispose();
    }
}
