using Helm.Core.Settings;

namespace Helm.Core.Mcp;

/// <summary>
/// Helm's MCP server (device-local, settings/mcp.json): whether Claude may use Helm's tools, and whether it may change
/// things or only read them. The Vault is never part of it.
/// </summary>
public sealed class McpSettings : IVersionedSettings
{
    public const string StoreId = "mcp";

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>Claude (Helm's chats, and Claude Code where it is added) can use the notes, Tracker and Missions tools.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Off: only the tools that read are offered.</summary>
    public bool AllowChanges { get; set; } = true;
}
