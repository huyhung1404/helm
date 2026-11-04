using Helm.Connector.Hosting;
using Helm.Core.Mcp;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Connector.Mcp;

/// <summary>
/// Helm's tools for Fairy Core, over MCP, from the same tool providers Ask Helm and Helm's own MCP server use. Nobody
/// is at the box to ask, so every call runs without a question, except what <see cref="McpTool.Destructive"/> marks (a
/// note to the trash, a mission's plan replaced): those are refused with a word to do it on the phone. Tools that
/// reach another machine are refused by <see cref="McpServer"/> itself, having no consent to ask. Vault's tools are
/// never registered here.
/// </summary>
internal static class ConnectorMcp
{
    public const string Instructions =
        "Helm is the user's own toolkit; this is its connector on the user's home server. These tools read and change the user's " +
        "Wallet (spending, debts, bills, goals), their Tracker (to-do lists), their Habits and, when the connector syncs them, their " +
        "Notes, Missions and Vocabulary, plus the briefs of Helm's automatic jobs. Changes sync to the user's phone and PC within " +
        "seconds. Dates and times are the user's local ones. Nothing here deletes or replaces the user's data: such tools answer with " +
        "an error, and the user does it on their phone. Ask before changing many things at once.";

    public static string Refusal(string tool) =>
        $"{tool} deletes or replaces the user's data, which the connector never does: there is nobody here to confirm it. " +
        "Ask the user to do it in Helm on their phone or PC.";

    /// <summary>The tools as the connector serves them.</summary>
    public static IReadOnlyList<McpTool> Tools(IEnumerable<IMcpToolProvider> providers) =>
        [.. providers.SelectMany(p => p.Tools).Select(t => t.Destructive
            ? t with { Run = (_, _) => throw new McpToolException(Refusal(t.Name)), Undo = null }
            : t)];

    public static McpServer Server(IEnumerable<IMcpToolProvider> providers, ILogger logger) =>
        new(Tools(providers), AppInfo.Version, Instructions, logger, consent: null);
}

/// <summary>The MCP socket as a part of serve (see <see cref="IServePart"/>).</summary>
internal sealed class McpPart(McpSocketHost host) : IServePart
{
    public string Path => host.Path;

    public void Start() => host.Start();

    public void Dispose() => host.Dispose();
}
