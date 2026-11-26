namespace Helm.Core.Ai;

/// <summary>
/// What an AI feature says when it cannot run. Helm has no AI settings page any more (models, keys, the proxy and the
/// features' switches are kept as they were set, but cannot be changed in Helm), so these never send the user to one:
/// they say plainly that the feature has no model, and the feature answers at once instead of waiting.
/// </summary>
public static class AiNotice
{
    /// <summary>No agent on a Helm PC is online and no API key is kept.</summary>
    public const string NoModel = "No AI model is set up for this, so it cannot run: no agent on a Helm PC is online and no API key is kept.";

    /// <summary>The feature was turned off (its switch is kept as it was set).</summary>
    public const string TurnedOff = "This AI feature is turned off, so it cannot run.";

    /// <summary>It needs an API key (photos, web search) and none is kept.</summary>
    public const string NeedsApiKey = "This needs an API key (Claude or Grok) and none is kept, so it cannot run.";

    /// <summary>The provider refused the kept key.</summary>
    public static string KeyRefused(string provider) => $"{provider} does not accept the kept API key, so it cannot run.";

    /// <summary>Where Helm's tools for AI agents are set (Claude Code, VS Code, Fairy): General › MCP on the PC.</summary>
    public const string McpPlace = "General › MCP";
}
