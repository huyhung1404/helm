using Helm.Core.Settings;

namespace Helm.Modules.Workspace;

public static class WorkspaceIds
{
    public const string ModuleId = "workspace-controller";
}

/// <summary>Device-local settings (never synced: they hold this PC's hook token and what it allows).</summary>
public sealed class WorkspaceSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>This install, so sessions and commands of several PCs never mix. Created on first use.</summary>
    public string InstallId { get; set; } = "";

    /// <summary>Loopback port Claude Code posts its hooks to. 0 = pick a free one on first start and keep it.</summary>
    public int HookPort { get; set; }

    /// <summary>Bearer token in the hook's header, so no other page or program can post fake events.</summary>
    public string HookToken { get; set; } = "";

    /// <summary>Helm keeps its hooks in Claude Code's user settings; turning this off takes them out.</summary>
    public bool ManageClaudeHooks { get; set; } = true;

    /// <summary>Helm installs and updates its small VS Code extension (Helm Bridge).</summary>
    public bool ManageBridgeExtension { get; set; } = true;
}
