using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Modules.NovelReader.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Helm.Modules.NovelReader;

/// <summary>
/// A hidden Microsoft Edge (headless, its own empty profile) that reads with Edge's own online voice HoaiMy: Edge
/// streams it and starts speaking in about 0.4 s, as on the web. Helm drives it through the DevTools protocol on a
/// random port bound to 127.0.0.1 and hears back through a page binding (start, end, error of each sentence). Started
/// on first use, stopped after 10 minutes without reading and when Helm exits (the whole process tree).
/// </summary>
public sealed class EdgeAnchor : IDisposable
{
    private const string Script = """
        window.__helm = {
          voice: null,
          live: new Map(),
          pick() {
            const voices = speechSynthesis.getVoices();
            this.voice = voices.find(v => v.name.includes('Hoài My')) || voices.find(v => v.lang === 'vi-VN' && !v.localService) || null;
            return this.voice;
          },
          ready() {
            return new Promise(done => { let n = 0; const tick = () => { if (this.pick() || ++n > 75) done(!!this.voice); else setTimeout(tick, 200); }; tick(); });
          },
          speak(id, text, rate, pitch, volume) {
            if (!this.voice) this.pick();
            const u = new SpeechSynthesisUtterance(text);
            u.voice = this.voice; u.lang = 'vi-VN'; u.rate = rate; u.pitch = pitch; u.volume = volume;
            const report = (type, error) => { if (type !== 'start') this.live.delete(id); helmEvent(JSON.stringify({ type, id, error })); };
            u.onstart = () => report('start');
            u.onend = () => report('end');
            u.onboundary = e => helmEvent(JSON.stringify({ type: 'boundary', id, at: e.charIndex }));
            u.onerror = e => report('error', e.error);
            this.live.set(id, u);   // kept alive until it ends (Chromium drops unreferenced utterances)
            speechSynthesis.speak(u);
          }
        };
        """;

    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);

    private readonly ISettingsStore<NovelReaderSettings> _settings;
    private readonly ILogger<EdgeAnchor> _logger;
    private readonly string _profile;
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _calls = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _speaking = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _starts = new();
    private readonly ConcurrentDictionary<string, Action<int>> _progress = new();
    private DateTime _lastActivity;
    private readonly Timer _idle;
    private Process? _process;
    private ClientWebSocket? _socket;
    private int _nextCall;
    private DateTime _lastUse = DateTime.UtcNow;
    private string _status = "Stopped";

    public EdgeAnchor(ISettingsStoreFactory settings, ILogger<EdgeAnchor> logger)
    {
        _settings = settings.Get<NovelReaderSettings>(NovelReaderIds.ModuleId);
        _logger = logger;
        _profile = Path.Combine(settings.Paths.Root, "cache", NovelReaderIds.ModuleId, "edge-profile");
        _idle = new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Edge is installed and the user did not turn this voice off.</summary>
    public bool IsAvailable => _settings.Current.UseEdgeVoice && EdgePath() is not null;

    public bool IsRunning => _socket?.State == WebSocketState.Open && _process is { HasExited: false };

    public string Status => _status;

    public event EventHandler? StatusChanged;

    /// <summary>
    /// Speaks after what is queued; completes when spoken. Cancelling stops it and everything queued. If Edge does not
    /// answer, it is started again once before the voice is reported unavailable.
    /// </summary>
    public async Task SpeakAsync(string text, double rate, double pitch, double volume, CancellationToken ct, Action<int>? progress = null)
    {
        try
        {
            await SpeakOnceAsync(text, rate, pitch, volume, ct, progress).ConfigureAwait(false);
        }
        catch (SpeechUnavailableException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Hidden Edge failed ({Reason}); starting it again", ex.Message);
            Stop();
            await SpeakOnceAsync(text, rate, pitch, volume, ct, progress).ConfigureAwait(false);
        }
    }

    private async Task SpeakOnceAsync(string text, double rate, double pitch, double volume, CancellationToken ct, Action<int>? progress)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        _lastUse = DateTime.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _speaking[id] = done;
        _starts[id] = started;
        if (progress is not null) _progress[id] = progress;
        using var registration = ct.Register(() =>
        {
            Cancel();
            done.TrySetCanceled(ct);
        });
        var call = $"__helm.speak({Js(id)}, {Js(text)}, {Num(Math.Clamp(rate, 0.5, 3))}, {Num(Math.Clamp(pitch, 0, 2))}, {Num(Math.Clamp(volume, 0, 1))})";
        try
        {
            await EvaluateAsync(call, awaitPromise: false, ct).ConfigureAwait(false);
            // Queued behind other sentences it waits for them; 8 s without any word spoken means Edge is stuck.
            while (!started.Task.IsCompleted && !done.Task.IsCompleted)
            {
                var before = _lastActivity;
                await Task.WhenAny(started.Task, done.Task, Task.Delay(TimeSpan.FromSeconds(8), ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!started.Task.IsCompleted && !done.Task.IsCompleted && _lastActivity == before)
                    throw new SpeechUnavailableException("Edge did not start speaking.");
            }
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            _speaking.TryRemove(id, out _);
            _starts.TryRemove(id, out _);
            _progress.TryRemove(id, out _);
            _lastUse = DateTime.UtcNow;
        }
    }

    public void Pause() => Fire("speechSynthesis.pause()");

    public void Resume() => Fire("speechSynthesis.resume()");

    /// <summary>Stops speaking; every queued sentence ends as cancelled.</summary>
    public void Cancel()
    {
        Fire("speechSynthesis.cancel()");
        foreach (var pending in _speaking.Values) pending.TrySetCanceled();
    }

    /// <summary>Starts Edge and waits for HoaiMy, so the first sentence starts quickly.</summary>
    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (IsRunning) return;
        await _starting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            Stop();
            var edge = EdgePath() ?? throw new SpeechUnavailableException("Microsoft Edge is not installed.");
            SetStatus("Starting Edge…");
            _logger.LogInformation("Starting the hidden Edge for HoaiMy");
            var port = FreePort();
            Directory.CreateDirectory(_profile);
            var start = new ProcessStartInfo(edge)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                "--headless=new", $"--remote-debugging-port={port}", "--remote-debugging-address=127.0.0.1", $"--user-data-dir={_profile}",
                "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync", "--autoplay-policy=no-user-gesture-required",
                "about:blank",
            }) start.ArgumentList.Add(arg);
            _process = Process.Start(start) ?? throw new SpeechUnavailableException("Microsoft Edge did not start.");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string? pageSocket = null;
            for (var i = 0; i < 100 && pageSocket is null; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var list = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false));
                    pageSocket = list.RootElement.EnumerateArray()
                        .Where(t => t.GetProperty("type").GetString() == "page")
                        .Select(t => t.GetProperty("webSocketDebuggerUrl").GetString())
                        .FirstOrDefault();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                }
                if (pageSocket is null) await Task.Delay(150, ct).ConfigureAwait(false);
            }
            if (pageSocket is null) throw new SpeechUnavailableException("Microsoft Edge did not answer.");

            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(pageSocket), ct).ConfigureAwait(false);
            _socket = socket;
            _ = ReceiveAsync(socket);
            await SendAsync("Runtime.enable", null, ct).ConfigureAwait(false);
            await SendAsync("Runtime.addBinding", new { name = "helmEvent" }, ct).ConfigureAwait(false);
            await EvaluateAsync(Script, awaitPromise: false, ct).ConfigureAwait(false);
            var ready = await EvaluateAsync("__helm.ready()", awaitPromise: true, ct).ConfigureAwait(false);
            if (!(ready.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True))
                throw new SpeechUnavailableException("Edge has no HoaiMy voice right now (it needs the internet).");
            _lastUse = DateTime.UtcNow;
            SetStatus("Running (hidden Edge)");
            _logger.LogInformation("Hidden Edge ready for HoaiMy on port {Port}", port);
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Stop();
            SetStatus("Could not start Edge: " + ex.Message);
            throw new SpeechUnavailableException("The hidden Edge could not start: " + ex.Message, ex);
        }
        catch (SpeechUnavailableException ex)
        {
            Stop();
            SetStatus(ex.Message);
            _logger.LogWarning("Hidden Edge could not start: {Reason}", ex.Message);
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Stop();
            SetStatus("Edge did not answer");
            _logger.LogWarning("Hidden Edge did not answer while starting");
            throw new SpeechUnavailableException("The hidden Edge did not answer.");
        }
        finally
        {
            _starting.Release();
        }
    }

    public void Stop()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        socket?.Abort();
        socket?.Dispose();
        var process = Interlocked.Exchange(ref _process, null);
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        process?.Dispose();
        foreach (var pending in _speaking.Values) pending.TrySetException(new SpeechUnavailableException("The hidden Edge stopped."));
        foreach (var call in _calls.Values) call.TrySetCanceled();
        _calls.Clear();
        SetStatus("Stopped");
    }

    public void Dispose()
    {
        _idle.Dispose();
        Stop();
    }

    /// <summary>Where Edge is installed (App Paths in the registry, or the usual folders).</summary>
    public static string? EdgePath()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
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

    private async Task<JsonElement> EvaluateAsync(string expression, bool awaitPromise, CancellationToken ct)
    {
        var result = await SendAsync("Runtime.evaluate", new { expression, awaitPromise, returnByValue = true }, ct).ConfigureAwait(false);
        if (result.TryGetProperty("exceptionDetails", out var error))
            throw new SpeechUnavailableException("Edge could not run the reader: " + error.GetRawText());
        return result.TryGetProperty("result", out var value) ? value : default;
    }

    private async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
    {
        var socket = _socket ?? throw new SpeechUnavailableException("The hidden Edge is not running.");
        var id = Interlocked.Increment(ref _nextCall);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = reply;
        var message = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });
        await _sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(message, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SpeechUnavailableException($"Edge did not answer ({method}).");
        }
        finally
        {
            _calls.TryRemove(id, out _);
        }
    }

    private void Fire(string expression)
    {
        if (!IsRunning) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await EvaluateAsync(expression, awaitPromise: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Edge call {Expression} failed", expression);
            }
        });
    }

    private async Task ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close) break;
                try
                {
                    Handle(message.ToArray());
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    _logger.LogWarning(ex, "Unexpected message from the hidden Edge");
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        if (ReferenceEquals(_socket, socket))
        {
            _logger.LogInformation("The hidden Edge closed");
            Stop();
        }
    }

    private void Handle(byte[] message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idElement) && _calls.TryGetValue(idElement.GetInt32(), out var reply))
        {
            if (root.TryGetProperty("error", out var error)) reply.TrySetException(new SpeechUnavailableException("Edge: " + error.GetRawText()));
            else reply.TrySetResult(root.GetProperty("result").Clone());
            return;
        }
        if (root.TryGetProperty("method", out var method) && method.GetString() == "Runtime.bindingCalled"
            && root.GetProperty("params").GetProperty("payload").GetString() is { } payload)
        {
            using var e = JsonDocument.Parse(payload);
            var type = e.RootElement.GetProperty("type").GetString();
            var id = e.RootElement.GetProperty("id").GetString() ?? "";
            // Any sign of life: Edge is speaking something, so sentences queued behind it are not stuck.
            _lastActivity = DateTime.UtcNow;
            if (type == "boundary")
            {
                if (_progress.TryGetValue(id, out var follow) && e.RootElement.TryGetProperty("at", out var at) && at.TryGetInt32(out var index))
                {
                    try
                    {
                        follow(index);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Following the spoken words failed");
                    }
                }
                return;
            }
            if (type == "start")
            {
                if (_starts.TryGetValue(id, out var started)) started.TrySetResult();
                return;
            }
            if (!_speaking.TryGetValue(id, out var done)) return;
            if (type == "end") done.TrySetResult();
            else if (type == "error")
            {
                var reason = e.RootElement.TryGetProperty("error", out var r) ? r.GetString() : null;
                if (reason is "canceled" or "interrupted") done.TrySetCanceled();
                else
                {
                    _logger.LogWarning("Hidden Edge could not speak: {Reason}", reason);
                    done.TrySetException(new SpeechUnavailableException($"Edge could not speak ({reason})."));
                }
            }
        }
    }

    private void StopIfIdle()
    {
        if (IsRunning && _speaking.IsEmpty && DateTime.UtcNow - _lastUse > IdleLimit)
        {
            _logger.LogInformation("Stopping the idle hidden Edge");
            Stop();
        }
    }

    private void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Js(string text) => JsonSerializer.Serialize(text);

    private static string Num(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>HoaiMy through the hidden Edge, as a voice of Novel Reader.</summary>
public sealed class EdgeAnchorEngine(EdgeAnchor anchor) : ISpeechEngine, IDirectSpeechEngine
{
    public const string EnginePrefix = "edgeapp";

    private static readonly IReadOnlyList<SpeechVoice> HoaiMy =
        [new SpeechVoice($"{EnginePrefix}:vi-VN-HoaiMy", "HoaiMy", "vi-VN", IsOnline: true) { IsFemale = true }];

    public string Prefix => EnginePrefix;

    public IReadOnlyList<SpeechVoice> Voices => anchor.IsAvailable ? HoaiMy : [];

    public void RefreshVoices() { }

    public Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct) =>
        throw new SpeechUnavailableException("HoaiMy through Edge plays by itself and cannot be downloaded.");

    public Task SpeakAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct, Action<int>? progress = null) =>
        anchor.SpeakAsync(text, options.Rate, options.Pitch, options.Volume, ct, progress);

    public void Pause() => anchor.Pause();

    public void Resume() => anchor.Resume();

    public void Cancel() => anchor.Cancel();

    public Task WarmUpAsync(CancellationToken ct) => anchor.EnsureStartedAsync(ct).ContinueWith(_ => { }, TaskScheduler.Default);
}
