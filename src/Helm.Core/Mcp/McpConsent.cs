namespace Helm.Core.Mcp;

/// <summary>What a tool call can do, and so whether the user is asked before it runs.</summary>
public enum McpRisk
{
    /// <summary>Only reads Helm's own data.</summary>
    Read,

    /// <summary>Changes Helm's data (synced to the other devices).</summary>
    Change,

    /// <summary>Runs something on another machine (SSH). Always goes through consent.</summary>
    Remote,
}

public enum McpDanger
{
    Normal,

    /// <summary>Shows the details prominently, and is never allowed for the rest of the session.</summary>
    High,
}

/// <summary>The question put to the user before one tool call runs.</summary>
/// <param name="Tool">The tool's name.</param>
/// <param name="Title">"Run “Deploy” on server “web”".</param>
/// <param name="WhatItDoes">Plain words: what happens if it is allowed.</param>
/// <param name="WhyAsk">The risk: why Helm asks instead of just doing it.</param>
/// <param name="Details">The exact command line or arguments, shown as they are.</param>
/// <param name="Target">The server, list… the call acts on.</param>
/// <param name="Elevated">Helm runs as Administrator, or the remote user is root.</param>
public sealed record McpConsentRequest(
    string Tool,
    McpRisk Risk,
    string Title,
    string WhatItDoes,
    string WhyAsk,
    string? Details,
    string? Target,
    bool Elevated,
    McpDanger Danger);

/// <summary>Who is calling: one MCP connection, named by the client's <c>initialize</c>.</summary>
public sealed record McpClientInfo(Guid ConnectionId, string Name, string? Version);

public enum McpConsentAnswer
{
    Deny,
    AllowOnce,
    AllowForSession,
}

/// <summary>Decides whether a tool call that is not <see cref="McpRisk.Read"/> may run (asking the user or not), and logs the answer.</summary>
public interface IMcpConsent
{
    /// <summary>Called by <see cref="McpServer"/> before the tool runs.</summary>
    Task<McpConsentAnswer> AskAsync(McpConsentRequest request, McpClientInfo client, CancellationToken ct);
}
