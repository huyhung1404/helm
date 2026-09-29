using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Sync;

/// <summary>
/// Keeps the sync server's live WebSocket (<c>GET /v1/sync/live</c>) open while wanted, and reports every seq it
/// announces, so the engine pulls right after another device pushed instead of at its next poll. The channel carries
/// only seq numbers; all data still comes through the normal pull.
/// </summary>
/// <remarks>
/// Reconnects with a growing, jittered delay after failures. A refused token (4001 or 401) waits until
/// <see cref="Reconnect"/> (new credentials) or ten minutes; a server without the channel is tried again after half an
/// hour. A "ping" every 30 s keeps middleboxes from dropping the idle socket, and silence for 75 s counts as a
/// dead connection.
/// </remarks>
internal sealed class SyncLiveChannel : IDisposable
{
    internal static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefusedBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UnavailableBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(10);
    private const int CloseRevoked = 4001;
    private const int MaxMessageBytes = 64 * 1024;

    private readonly ISyncLiveTransport _transport;
    private readonly Func<bool> _canConnect;
    private readonly Action<long> _onSeq;
    private readonly Action<bool> _onConnected;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _connection;
    private volatile bool _connected;

    /// <param name="canConnect">False while sync is not set up (no token or no key): the channel waits.</param>
    /// <param name="onSeq">A seq the server announced (on connect and after every accepted push).</param>
    public SyncLiveChannel(ISyncLiveTransport transport, Func<bool> canConnect, Action<long> onSeq, Action<bool> onConnected, ILogger logger)
    {
        _transport = transport;
        _canConnect = canConnect;
        _onSeq = onSeq;
        _onConnected = onConnected;
        _logger = logger;
    }

    public bool Connected => _connected;

    public void Start()
    {
        lock (_gate)
        {
            if (_stop is not null) return;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _ = Task.Run(() => RunAsync(token));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stop?.Cancel();
            _stop?.Dispose();
            _stop = null;
        }
    }

    /// <summary>Drops the current connection and connects again at once (new credentials or key).</summary>
    public void Reconnect()
    {
        lock (_gate)
        {
            try { _connection?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        Wake();
    }

    public void Dispose() => Stop();

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var backoff = FirstBackoff;
        while (!stop.IsCancellationRequested)
        {
            bool ready;
            try { ready = _canConnect(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live sync could not check its settings");
                ready = false;
            }
            if (!ready)
            {
                await WaitAsync(IdleCheck, stop).ConfigureAwait(false);
                continue;
            }

            TimeSpan delay;
            var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_gate) _connection = connection;
            try
            {
                using var socket = await _transport.ConnectLiveAsync(connection.Token).ConfigureAwait(false);
                SetConnected(true);
                backoff = FirstBackoff;
                var closedWith = await ListenAsync(socket, connection.Token).ConfigureAwait(false);
                delay = closedWith == CloseRevoked ? RefusedBackoff : FirstBackoff;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                delay = TimeSpan.Zero; // Reconnect()
            }
            catch (SyncAuthException)
            {
                _logger.LogInformation("Live sync: the server refused this token; waiting for new credentials");
                delay = RefusedBackoff;
            }
            catch (SyncLiveUnavailableException)
            {
                _logger.LogInformation("Live sync: this server has no live channel; polling only");
                delay = UnavailableBackoff;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Live sync connection failed");
                delay = backoff;
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
            finally
            {
                lock (_gate) _connection = null;
                connection.Dispose();
                SetConnected(false);
            }
            await WaitAsync(Jitter(delay), stop).ConfigureAwait(false);
        }
    }

    /// <returns>The close status the server sent, or null when the connection just ended.</returns>
    private async Task<int?> ListenAsync(WebSocket socket, CancellationToken ct)
    {
        var lastHeard = Environment.TickCount64;
        using var pinging = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = PingAsync(socket, () => Environment.TickCount64 - Volatile.Read(ref lastHeard), pinging.Token);
        try
        {
            var buffer = new byte[4096];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                Volatile.Write(ref lastHeard, Environment.TickCount64);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { }
                    return (int?)result.CloseStatus;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes) throw new InvalidDataException("Live sync message too large.");
                if (!result.EndOfMessage) continue;
                if (result.MessageType == WebSocketMessageType.Text) Handle(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
            return (int?)socket.CloseStatus;
        }
        finally
        {
            pinging.Cancel();
            try { await pinger.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task PingAsync(WebSocket socket, Func<long> silentMs, CancellationToken ct)
    {
        var ping = Encoding.ASCII.GetBytes("ping");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PingInterval, ct).ConfigureAwait(false);
            if (silentMs() > SilenceLimit.TotalMilliseconds)
            {
                // Nothing came back, not even "pong": the connection is dead without anyone having closed it.
                socket.Abort();
                return;
            }
            try { await socket.SendAsync(ping, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { return; }
        }
    }

    private void Handle(string text)
    {
        if (text == "pong") return;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var value) && value > 0) _onSeq(value);
        }
        catch (JsonException)
        {
            // Not ours; ignore.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A live sync handler failed");
        }
    }

    private void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        try { _onConnected(connected); }
        catch (Exception ex) { _logger.LogError(ex, "A live sync state handler failed"); }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken stop)
    {
        if (delay <= TimeSpan.Zero) return;
        try { await _wake.WaitAsync(delay, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>±20 %, so many devices that lost the server together do not all come back in the same second.</summary>
    private static TimeSpan Jitter(TimeSpan delay) =>
        delay <= TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (0.8 + Random.Shared.NextDouble() * 0.4));
}
