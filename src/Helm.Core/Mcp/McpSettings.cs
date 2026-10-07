using Helm.Core.Settings;

namespace Helm.Core.Mcp;

/// <summary>
/// Helm's MCP server (device-local, settings/mcp.json): whether AI agents may use Helm's tools, and whether it may change
/// things or only read them. The Vault is never part of it.
/// </summary>
public sealed class McpSettings : IVersionedSettings
{
    public const string StoreId = "mcp";

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>AI agents (Claude Code, VS Code or any MCP client where Helm is added) can use the tools of the modules that are on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Off: only the tools that read are offered.</summary>
    public bool AllowChanges { get; set; } = true;

    /// <summary>
    /// Every call that changes Helm's data asks first, even while Helm is not elevated (elevated, it always asks).
    /// Added in version 1 with a false default, so no migration.
    /// </summary>
    public bool AskBeforeChanges { get; set; }
}
