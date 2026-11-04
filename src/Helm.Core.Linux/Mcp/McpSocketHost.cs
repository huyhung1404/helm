using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Mcp;

/// <summary>
/// Serves Helm's MCP tools on a Unix domain socket, the Linux side of <c>McpPipeHost</c>: each connection gets its own
/// <see cref="McpServer"/>, built when it connects. The socket is the owner's and its group's (0660), in a folder that
/// decides who may reach it (the connector's 0700 data folder, or a service's runtime folder shared with one group).
/// One process serves it: a second one finds the socket answering and does not start.
/// </summary>
public sealed class McpSocketHost(string path, Func<McpServer> serverFactory, ILogger logger, int maxConnections = McpSocketHost.DefaultMaxConnections)
    : IDisposable
{
    public const int DefaultMaxConnections = 8;

    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, Socket> _connections = new();
    private Socket? _listener;
    private Task? _loop;

    public string Path { get; } = path;

    public int Connections => _connections.Count;

    /// <summary>The longest socket path Linux takes (sun_path, with its final zero).</summary>
    public const int MaxPathLength = 107;

    /// <exception cref="InvalidOperationException">Another process serves this socket already, or its path is too long.</exception>
    public void Start()
    {
        if (_listener is not null) return;
        if (System.Text.Encoding.UTF8.GetByteCount(Path) > MaxPathLength)
            throw new InvalidOperationException($"The MCP socket path is longer than {MaxPathLength} bytes ({Path}): set HELM_MCP_SOCKET to a shorter one.");
        Linux.PrivateFiles.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (File.Exists(Path))
        {
            if (Answers(Path)) throw new InvalidOperationException($"Another helm-connector serves MCP at {Path}.");
            File.Delete(Path); // left by a process that did not stop cleanly
        }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(Path));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            listener.Listen(maxConnections + 1);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _listener = listener;
        _loop = Task.Run(() => AcceptAsync(listener, _stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Dispose();
        foreach (var connection in _connections.Values) connection.Dispose();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        if (_listener is not null)
        {
            try { File.Delete(Path); }
            catch (IOException) { }
        }
        _stop.Dispose();
    }

    /// <summary>True when something accepts connections on <paramref name="socketPath"/> now.</summary>
    public static bool Answers(string socketPath)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task AcceptAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (ct.IsCancellationRequested) return;
                logger.LogWarning(ex, "MCP socket: accepting a connection failed");
                continue;
            }
            if (_connections.Count >= maxConnections)
            {
                logger.LogWarning("MCP socket: a caller was refused, {Max} are connected already", maxConnections);
                socket.Dispose();
                continue;
            }
            _ = ServeAsync(socket, ct);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _connections[id] = socket;
        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            var server = serverFactory();
            logger.LogInformation("MCP client connected ({Tools} tools)", server.ToolNames.Count);
            await server.RunAsync(stream, stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The client went away, or serve is stopping.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP connection failed");
        }
        finally
        {
            _connections.TryRemove(id, out _);
            logger.LogInformation("MCP client disconnected");
        }
    }
}
