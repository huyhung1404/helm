using System.Security.Cryptography;
using System.Text;
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

/// <summary>Where Helm's MCP server listens: a named pipe per data folder, so a test copy never answers for the real one.</summary>
public static class McpEndpoint
{
    public static string PipeName(HelmPaths paths)
    {
        var root = Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        return "Helm.Mcp." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)), 0, 8);
    }

    /// <summary>What Claude is told about Helm when it connects.</summary>
    public const string Instructions =
        "Helm is the user's own toolkit. These tools read and change the user's notes, their Tracker (to-do lists " +
        "and a debt book: who owes whom) and their Missions (goals reached through ordered steps). Changes sync to " +
        "the user's other devices. Dates and times are the user's " +
        "local ones. Nothing here deletes for good: notes go to a 30-day trash. Ask before changing many things at once.";
}
