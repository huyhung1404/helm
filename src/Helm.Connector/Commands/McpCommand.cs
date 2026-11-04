using System.Net.Sockets;
using Helm.Core.Settings;

namespace Helm.Connector.Commands;

/// <summary>
/// <c>helm-connector mcp</c>: Helm's tools as an MCP server on stdio, for Fairy Core. It only relays to the running
/// <c>serve</c> over its socket: one process owns the sync replica and its stores, so a tool call here and serve's
/// sync never write the same database from two processes, and a change goes up as soon as serve's engine sees it.
/// </summary>
internal sealed class McpCommand : ICliCommand
{
    public string Name => "mcp";

    public string Summary => "Helm's tools over MCP on stdio (relays to the running serve)";

    public async Task<int> RunAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count > 0)
        {
            context.Console.Error.WriteLine($"Usage: {ConnectorApp.Name} mcp");
            return ConnectorApp.Usage;
        }
        var path = ConnectorPaths.McpSocket(new HelmPaths(context.DataDirectory), context.Console.Environment);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            context.Console.Error.WriteLine($"{ConnectorApp.Name} mcp: {ConnectorApp.Name} serve is not running here (no MCP socket at {path}: {ex.Message}).");
            return ConnectorApp.Failed;
        }
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        await using var input = context.Console.OpenInput();
        await using var output = context.Console.OpenOutput();
        var up = Task.Run(async () =>
        {
            try
            {
                await input.CopyToAsync(stream, ct);
            }
            finally
            {
                // The client is done: serve answers what is left, then closes its end.
                try { socket.Shutdown(SocketShutdown.Send); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            }
        }, ct);
        try
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                await output.FlushAsync(ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
        return ConnectorApp.Ok;
    }
}
