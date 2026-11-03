using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Helm.Ai.Vps;

/// <summary>One connection to the VPS's worker endpoint, carrying JSON text frames. A new one is made per attempt.</summary>
public interface IVpsSocket : IDisposable
{
    /// <exception cref="VpsSocketException">The VPS refused it (wrong token…) or could not be reached; said to the user.</exception>
    Task ConnectAsync(Uri url, string token, CancellationToken ct);

    /// <summary>Sends one frame; one at a time (the worker sends in turn).</summary>
    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next frame, or null once the VPS closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
}

/// <summary>Why a connection to the VPS failed, as the Proxy tab says it.</summary>
public sealed class VpsSocketException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The real connection: a <see cref="ClientWebSocket"/> with the worker token as its bearer.</summary>
public sealed class VpsWebSocket : IVpsSocket
{
    /// <summary>A job is a prompt, never a file: a larger frame is the VPS misbehaving.</summary>
    internal const int MaxFrame = 4 * 1024 * 1024;

    private readonly ClientWebSocket _socket = new();

    public async Task ConnectAsync(Uri url, string token, CancellationToken ct)
    {
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await _socket.ConnectAsync(url, ct);
        }
        catch (WebSocketException ex)
        {
            throw _socket.HttpStatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new VpsSocketException("the VPS did not accept the worker token", ex),
                HttpStatusCode.NotFound => new VpsSocketException("the VPS has no worker endpoint at this address", ex),
                (HttpStatusCode)0 => new VpsSocketException($"could not reach {url.Host}", ex),
                var status => new VpsSocketException($"the VPS answered {(int)status}", ex),
            };
        }
    }

    public Task SendAsync(string text, CancellationToken ct) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult part;
            try
            {
                part = await _socket.ReceiveAsync(buffer, ct);
            }
            catch (WebSocketException)
            {
                return null;
            }
            if (part.MessageType == WebSocketMessageType.Close) return null;
            frame.Write(buffer, 0, part.Count);
            if (frame.Length > MaxFrame) throw new VpsSocketException("the VPS sent a frame too large to be a job");
            if (part.EndOfMessage) return part.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length) : "";
        }
    }

    public void Dispose()
    {
        if (_socket.State == WebSocketState.Open)
        {
            // A polite close, not awaited for long: the worker is stopping or starting over.
            try
            {
                _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is WebSocketException or AggregateException or ObjectDisposedException)
            {
                // Gone already.
            }
        }
        _socket.Dispose();
    }
}
