using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Connector.Mcp;
using Helm.Core.Settings;

namespace Helm.Connector.Commands;

/// <summary>
/// <c>helm-connector pending</c>, <c>confirm ID</c> and <c>cancel ID</c>: the user's answer to a deletion waiting in the
/// running serve (<see cref="ConfirmationGate"/>), from a terminal, as Fairy Core gives it in code. One JSON-RPC request
/// on serve's socket.
/// </summary>
internal sealed class GateCommand(string name, string method, string summary, bool takesId) : ICliCommand
{
    public static IEnumerable<ICliCommand> All() =>
    [
        new GateCommand("pending", ConfirmationGate.List, "List the deletions waiting for the user's yes", takesId: false),
        new GateCommand("confirm", ConfirmationGate.Confirm, "Say yes to a waiting deletion: it runs now (confirm ID)", takesId: true),
        new GateCommand("cancel", ConfirmationGate.Cancel, "Say no to a waiting deletion (cancel ID)", takesId: true),
    ];

    public string Name => name;

    public string Summary => summary;

    public async Task<int> RunAsync(CliContext context, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count != (takesId ? 1 : 0))
        {
            context.Console.Error.WriteLine($"Usage: {ConnectorApp.Name} {name}{(takesId ? " ID" : "")}");
            return ConnectorApp.Usage;
        }
        var path = ConnectorPaths.McpSocket(new HelmPaths(context.DataDirectory), context.Console.Environment);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentOutOfRangeException)
        {
            context.Console.Error.WriteLine($"{ConnectorApp.Name} {name}: {ConnectorApp.Name} serve is not running here (no MCP socket at {path}).");
            return ConnectorApp.Failed;
        }
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = takesId ? new JsonObject { ["confirmationId"] = args[0] } : null };
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"), ct);
        socket.Shutdown(SocketShutdown.Send);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var reply = JsonNode.Parse(await reader.ReadLineAsync(ct) ?? "{}");
        if (reply?["error"]?["message"]?.GetValue<string>() is { } error)
        {
            context.Console.Error.WriteLine($"{ConnectorApp.Name} {name}: {error}");
            return ConnectorApp.Failed;
        }
        context.Console.Out.WriteLine(Text(reply?["result"]));
        return reply?["result"]?["isError"]?.GetValue<bool>() == true ? ConnectorApp.Failed : ConnectorApp.Ok;
    }

    /// <summary>The result as a person reads it: a tool's text, the waiting list one per line, else the JSON.</summary>
    private static string Text(JsonNode? result)
    {
        if (result?["content"]?[0]?["text"]?.GetValue<string>() is { } text) return text;
        if (result?["pending"] is JsonArray pending)
            return pending.Count == 0 ? "Nothing waits for a confirmation."
                : string.Join(Environment.NewLine, pending.Select(p => $"{p!["confirmationId"]}  {p["tool"]}  {p["summary"]}  (until {p["expiresAt"]})"));
        return result?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "";
    }
}
