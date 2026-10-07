using Helm.Core.Mcp;
using Microsoft.Extensions.Logging;

namespace Helm.App.Mcp;

/// <summary>
/// Decides whether Claude's calls may run. For now it keeps what Helm always did: changes to Helm's data run without a
/// question, and nothing runs on another machine.
/// </summary>
internal sealed class McpConsentService(ILogger<McpConsentService> logger) : IMcpConsent
{
    public Task<McpConsentAnswer> AskAsync(McpConsentRequest request, McpClientInfo client, CancellationToken ct)
    {
        var answer = request.Risk == McpRisk.Remote ? McpConsentAnswer.Deny : McpConsentAnswer.AllowOnce;
        logger.LogInformation("MCP {Client}: {Tool} ({Risk}) {Answer}", client.Name, request.Tool, request.Risk, answer);
        return Task.FromResult(answer);
    }
}
