using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Workspace;

/// <summary>
/// Receives Claude Code's <c>http</c> hooks on 127.0.0.1. A tiny HTTP/1.1 server on purpose (no URL ACL, no web
/// stack): it accepts only <c>POST <see cref="ClaudeHookConfig.UrlPath"/></c> with the right bearer token, never from
/// a browser (requests with an <c>Origin</c> header are refused), and answers <c>{}</c> at once so Claude never waits.
/// </summary>
public sealed class HookListener : IAsyncDisposable
{
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxBodyBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly byte[] _token;
    private readonly Func<ClaudeHookEvent, Task<string?>> _onEvent;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private HookListener(TcpListener listener, string token, Func<ClaudeHookEvent, Task<string?>> onEvent, ILogger logger)
    {
        _listener = listener;
        _token = Encoding.UTF8.GetBytes("Bearer " + token);
        _onEvent = onEvent;
        _logger = logger;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Listens on <paramref name="port"/> (0 = any free one).</summary>
    /// <exception cref="SocketException">The port is taken.</exception>
    /// <param name="onEvent">Handles a hook and returns the JSON Claude gets back (null: <c>{}</c>, nothing to say).
    /// It may take long: a held PermissionRequest waits for the phone.</param>
    public static HookListener Start(int port, string token, Func<ClaudeHookEvent, Task<string?>> onEvent, ILogger logger)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        listener.Start();
        var host = new HookListener(listener, token, onEvent, logger);
        host._loop = host.AcceptAsync(host._stop.Token);
        return host;
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (ct.IsCancellationRequested) return;
                _logger.LogWarning(ex, "Workspace hook listener: accept failed");
                continue;
            }
            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                int status;
                ClaudeHookEvent? hook;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(ReadTimeout);
                    (status, hook) = await ReadAsync(stream, timeout.Token).ConfigureAwait(false);
                }
                var body = hook is null ? null : await _onEvent(hook).ConfigureAwait(false);
                using var write = CancellationTokenSource.CreateLinkedTokenSource(ct);
                write.CancelAfter(ReadTimeout);
                await RespondAsync(stream, status, body, write.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // A client that went away or was too slow.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workspace hook listener: a hook could not be handled");
            }
        }
    }

    /// <summary>Parses one request; the status says why it was refused (the hook is then null).</summary>
    internal async Task<(int Status, ClaudeHookEvent? Hook)> ReadAsync(Stream stream, CancellationToken ct)
    {
        var head = new MemoryStream();
        var one = new byte[1];
        var tail = 0u;
        while (true)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) return (400, null);
            head.WriteByte(one[0]);
            tail = (tail << 8) | one[0];
            if (tail == 0x0D0A0D0A) break;
            if (head.Length > MaxHeaderBytes) return (431, null);
        }
        var lines = Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length).Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length < 2) return (400, null);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (!headers.TryGetValue("Content-Length", out var lengthText) || !int.TryParse(lengthText, out var length) || length < 0) return (411, null);
        if (length > MaxBodyBytes) return (413, null);
        // Read the body even when refusing: closing with unread data resets the connection and loses the answer.
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        if (request[0] != "POST") return (405, null);
        if (request[1] != ClaudeHookConfig.UrlPath) return (404, null);
        // A web page could reach 127.0.0.1 too; Claude Code never sends Origin.
        if (headers.ContainsKey("Origin")) return (403, null);
        if (!headers.TryGetValue("Authorization", out var auth) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth), _token)) return (401, null);
        var hook = ClaudeHookEvent.Parse(body);
        return hook is null ? (400, null) : (200, hook);
    }

    private static async Task RespondAsync(Stream stream, int status, string? answer, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(status == 200 ? answer ?? "{}" : "");
        var reason = status switch { 200 => "OK", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", _ => "Bad Request" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
    }
}
