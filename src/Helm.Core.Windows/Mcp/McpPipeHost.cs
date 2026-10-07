using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Mcp;

/// <summary>One client connected to Helm's MCP server now.</summary>
public sealed record McpConnectionInfo(Guid Id, string ClientName, string? ClientVersion, DateTimeOffset Since, int Calls);

/// <summary>
/// Serves Helm's MCP tools on a named pipe that only this Windows user can open (Helm runs elevated, Claude Code does
/// not, so the pipe grants the user's own SID explicitly). <c>Helm.exe --mcp</c> relays Claude's stdio to it. Each
/// connection gets its own <see cref="McpServer"/>, built when it connects, so a change of the settings applies to the
/// next connection.
/// </summary>
public sealed class McpPipeHost : IDisposable
{
    private const int MaxConnections = 8;
    private readonly string _pipeName;
    private readonly Func<McpServer?> _serverFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private Task? _loop;

    /// <param name="serverFactory">Null when the server is turned off: the connection is closed at once.</param>
    public McpPipeHost(HelmPaths paths, Func<McpServer?> serverFactory, ILogger<McpPipeHost> logger)
    {
        _pipeName = McpEndpoint.PipeName(paths);
        _serverFactory = serverFactory;
        _logger = logger;
    }

    public string PipeName => _pipeName;

    /// <summary>The clients connected now, oldest first.</summary>
    public IReadOnlyList<McpConnectionInfo> Connections => _connections.Values
        .Select(c => new McpConnectionInfo(c.Server.Client.ConnectionId, c.Server.Client.Name, c.Server.Client.Version, c.Since, c.Server.Calls))
        .OrderBy(c => c.Since)
        .ToList();

    /// <summary>A client connected, introduced itself, called a tool or left. Raised off the UI thread.</summary>
    public event EventHandler? ConnectionsChanged;

    /// <summary>Closes that client's connection (it may connect again; the next call then starts a new one).</summary>
    public void Disconnect(Guid id)
    {
        if (!_connections.TryGetValue(id, out var connection)) return;
        try { connection.Stop.Cancel(); }
        catch (ObjectDisposedException) { } // it was just leaving
    }

    public void Start() => _loop ??= Task.Run(() => AcceptAsync(_stop.Token));

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _stop.Dispose();
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, MaxConnections, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, Security());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // All instances busy, or another Helm has the name: try again a little later.
                _logger.LogWarning(ex, "MCP pipe {Pipe} could not be created", _pipeName);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            _ = ServeAsync(pipe, ct);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        Connection? connection = null;
        await using (pipe)
        {
            try
            {
                if (_serverFactory() is not { } server) return;
                connection = new Connection(server, DateTimeOffset.Now, CancellationTokenSource.CreateLinkedTokenSource(ct));
                _connections[server.Client.ConnectionId] = connection;
                server.Changed += OnConnectionChanged;
                OnConnectionChanged(server, EventArgs.Empty);
                _logger.LogInformation("MCP client connected ({Tools} tools)", server.ToolNames.Count);
                await server.RunAsync(pipe, pipe, connection.Stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MCP connection failed");
            }
        }
        if (connection is null) return;
        connection.Server.Changed -= OnConnectionChanged;
        _connections.TryRemove(connection.Server.Client.ConnectionId, out _);
        connection.Stop.Dispose();
        OnConnectionChanged(connection.Server, EventArgs.Empty);
        _logger.LogInformation("MCP client disconnected");
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
    {
        try
        {
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A listener's failure must not end the connection.
            _logger.LogError(ex, "MCP connection listener failed");
        }
    }

    private sealed record Connection(McpServer Server, DateTimeOffset Since, CancellationTokenSource Stop);

    /// <summary>The user's own SID only (the same in Helm's elevated token and in Claude Code's normal one).</summary>
    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No user SID.");
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        // Never from another machine, even for this user.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }
}

/// <summary>
/// <c>Helm.exe --mcp</c>: what Claude Code starts. It relays its standard input and output to the running Helm's MCP
/// pipe. Without a running Helm it still answers, with no tools and a note to start Helm.
/// </summary>
public static class McpBridge
{
    public static async Task<int> RunAsync(HelmPaths paths, string version)
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        NamedPipeClientStream pipe;
        try
        {
            pipe = await ConnectAsync(paths, 3000).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            var offline = new McpServer([], version,
                "Helm is not running (or its tools for Claude are turned off), so there are no Helm tools right now. " +
                "Ask the user to start Helm and to check Claude Chat → Helm tools for Claude.");
            await offline.RunAsync(stdin, stdout, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        await using (pipe)
        {
            using var done = new CancellationTokenSource();
            var up = RelayAsync(stdin, pipe, done.Token);
            var down = RelayAsync(pipe, stdout, done.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false);
            await done.CancelAsync().ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>
    /// Connects to Helm's pipe, and only if this user owns it (another user cannot take the name first).
    /// Not <see cref="PipeOptions.CurrentUserOnly"/>: .NET compares the pipe's owner with the token's default owner,
    /// which in an elevated token is Administrators, not the user, so an elevated Claude (started by the elevated
    /// Helm) would be refused. Helm's pipe is owned by the user's SID; either SID of this token is accepted.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The pipe belongs to someone else.</exception>
    public static async Task<NamedPipeClientStream> ConnectAsync(HelmPaths paths, int timeoutMs)
    {
        var pipe = new NamedPipeClientStream(".", McpEndpoint.PipeName(paths), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs).ConfigureAwait(false);
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            using var me = WindowsIdentity.GetCurrent();
            if (owner is null || (owner != me.User && owner != me.Owner))
                throw new UnauthorizedAccessException($"The pipe {McpEndpoint.PipeName(paths)} is not owned by this user.");
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RelayAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // One side closed.
        }
    }
}
