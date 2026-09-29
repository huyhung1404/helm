using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Core.Mcp;

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
    private Task? _loop;

    /// <param name="serverFactory">Null when the server is turned off: the connection is closed at once.</param>
    public McpPipeHost(HelmPaths paths, Func<McpServer?> serverFactory, ILogger<McpPipeHost> logger)
    {
        _pipeName = McpEndpoint.PipeName(paths);
        _serverFactory = serverFactory;
        _logger = logger;
    }

    public string PipeName => _pipeName;

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
        await using (pipe)
        {
            try
            {
                if (_serverFactory() is not { } server) return;
                _logger.LogInformation("MCP client connected ({Tools} tools)", server.ToolNames.Count);
                await server.RunAsync(pipe, pipe, ct).ConfigureAwait(false);
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
        _logger.LogInformation("MCP client disconnected");
    }

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

/// <summary>
/// How Claude reaches Helm's tools: the command it starts (this Helm.exe with --mcp), as an MCP config file for Helm's
/// own chats and as a <c>claude mcp add</c> line for Claude Code in a terminal.
/// </summary>
public sealed class McpClientConfig(HelmPaths paths, ISettingsStoreFactory settings)
{
    public bool Enabled => settings.Get<McpSettings>(McpSettings.StoreId).Current.Enabled;

    /// <summary>The running Helm.exe (for an installed Helm, HelmApp\current\Helm.exe, which updates keep in place).</summary>
    public string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Helm.exe");

    /// <summary>"--mcp", plus the data folder when it is not the usual one (a test copy).</summary>
    public IReadOnlyList<string> Arguments =>
        string.Equals(Path.GetFullPath(paths.Root), Path.GetFullPath(new HelmPaths().Root), StringComparison.OrdinalIgnoreCase)
            ? ["--mcp"]
            : ["--mcp", "--data-dir", paths.Root];

    /// <summary>For a terminal: adds Helm to Claude Code for every folder (user scope).</summary>
    public string ClaudeCodeCommand => $"claude mcp add --scope user helm -- {Quote(Executable)} {string.Join(' ', Arguments.Select(Quote))}";

    /// <summary>Writes the config Helm's chats pass with --mcp-config; null while the tools are off.</summary>
    public string? WriteConfigFile()
    {
        if (!Enabled) return null;
        var folder = Path.Combine(paths.Root, "mcp");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "claude-mcp.json");
        var config = new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                ["helm"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = Executable,
                    ["args"] = new System.Text.Json.Nodes.JsonArray(Arguments.Select(a => (System.Text.Json.Nodes.JsonNode)a).ToArray()),
                },
            },
        };
        File.WriteAllText(file, config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    private static string Quote(string arg) => arg.Contains(' ') || arg.Contains('"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
}
