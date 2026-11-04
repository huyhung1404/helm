using Helm.Core.Settings;

namespace Helm.Connector;

/// <summary>The connector's own files beside Helm's data: what a running serve says about itself, and its MCP socket.</summary>
internal static class ConnectorPaths
{
    /// <summary>Where serve listens for <c>helm-connector mcp</c> instead of &lt;data&gt;/run/mcp.sock (e.g. under /run for a service).</summary>
    public const string SocketVariable = "HELM_MCP_SOCKET";

    public static string RunDirectory(HelmPaths paths) => Path.Combine(paths.Root, "run");

    public static string StateFile(HelmPaths paths) => Path.Combine(RunDirectory(paths), "serve.json");

    public static string McpSocket(HelmPaths paths, Func<string, string?> environment) =>
        environment(SocketVariable) is { Length: > 0 } socket ? socket : Path.Combine(RunDirectory(paths), "mcp.sock");
}
