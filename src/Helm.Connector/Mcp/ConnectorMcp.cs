using Helm.Connector.Hosting;
using Helm.Core.Mcp;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Connector.Mcp;

/// <summary>
/// Helm's tools for Fairy Core, over MCP, from the same tool providers Ask Helm and Helm's own MCP server use. Nobody
/// is at the box to ask, so a call runs without a question, except what <see cref="McpTool.Destructive"/> marks (a note
/// to the trash, a mission replanned, any bulk deletion): those go through the <see cref="ConfirmationGate"/> and run
/// only after the user's yes, confirmed by the host over <c>helm/confirm</c>. Tools that reach another machine are
/// refused by <see cref="McpServer"/> itself, having no consent to ask. Vault's tools are never registered here.
/// </summary>
internal static class ConnectorMcp
{
    public const string Instructions =
        "Helm is the user's own toolkit; this is its connector on the user's home server. These tools read and change the user's " +
        "Wallet (spending, debts, bills, goals), their Tracker (to-do lists), their Habits and, when the connector syncs them, their " +
        "Notes, Missions and Vocabulary, plus the briefs of Helm's automatic jobs. Changes sync to the user's phone and PC within " +
        "seconds. Dates and times are the user's local ones. A tool that deletes or replaces data answers \"needs_confirmation\" " +
        "with a summary and changes nothing: tell the user what will happen; it runs only once they say yes. Ask before changing " +
        "many things at once.";

    /// <summary>The tools as the connector serves them.</summary>
    public static IReadOnlyList<McpTool> Tools(IEnumerable<IMcpToolProvider> providers, ConfirmationGate gate) =>
        [.. providers.SelectMany(p => p.Tools).Select(t => t.Destructive ? gate.Guard(t) : t)];

    public static McpServer Server(IEnumerable<IMcpToolProvider> providers, ConfirmationGate gate, ILogger logger) =>
        new(Tools(providers, gate), AppInfo.Version, Instructions, logger, consent: null) { Methods = gate.Methods };
}

/// <summary>The MCP socket as a part of serve (see <see cref="IServePart"/>).</summary>
internal sealed class McpPart(McpSocketHost host) : IServePart
{
    public string Path => host.Path;

    public void Start() => host.Start();

    public void Dispose() => host.Dispose();
}
