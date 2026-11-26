using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Helm.Modules.SlackBot;

/// <summary>
/// Slack's Socket Mode: one WebSocket Helm opens to Slack (nothing listens on this PC), over which every event of the
/// app arrives. Each envelope is acknowledged at once, as Slack requires, then handed on. Slack closes the socket now
/// and then ("disconnect"); <see cref="RunAsync"/> then opens a new one, as it does after any error, waiting a little
/// longer each time.
/// </summary>
public sealed class SlackSocket(ISlackApi api)
{
    /// <summary>Raised (on a worker thread) with true once Slack says hello, false when the socket is lost.</summary>
    public event EventHandler<bool>? ConnectedChanged;

    /// <summary>Runs until <paramref name="ct"/>: every event goes to <paramref name="onEvent"/>, which must not throw.</summary>
    /// <param name="onConnected">After each (re)connection: the time to catch up on what was missed meanwhile.</param>
    public async Task RunAsync(Func<JsonElement, Task> onEvent, Func<Task> onConnected, Action<Exception> onError, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = await api.OpenSocketAsync(ct).ConfigureAwait(false);
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await socket.ConnectAsync(url, ct).ConfigureAwait(false);
                await ReadAsync(socket, onEvent, async () =>
                {
                    delay = TimeSpan.FromSeconds(2);
                    ConnectedChanged?.Invoke(this, true);
                    await onConnected().ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SlackApiException ex) when (ex.IsAuthProblem)
            {
                ConnectedChanged?.Invoke(this, false);
                throw;
            }
            catch (Exception ex)
            {
                onError(ex);
            }
            ConnectedChanged?.Invoke(this, false);
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = TimeSpan.FromSeconds(Math.Min(120, delay.TotalSeconds * 2));
        }
    }

    private static async Task ReadAsync(ClientWebSocket socket, Func<JsonElement, Task> onEvent, Func<Task> onHello, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var bytes = message.ToArray();
            message.SetLength(0);
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.TryGetProperty("envelope_id", out var id) && id.GetString() is { } envelope)
            {
                var ack = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { envelope_id = envelope }));
                await socket.SendAsync(ack, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "hello":
                    // Catching up runs beside the socket, so events keep being acknowledged meanwhile.
                    _ = onHello();
                    break;
                case "disconnect":
                    return;
                case "events_api" when root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("event", out var e):
                    await onEvent(e.Clone()).ConfigureAwait(false);
                    break;
            }
        }
    }
}
