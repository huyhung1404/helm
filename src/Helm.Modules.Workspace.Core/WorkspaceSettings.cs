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

    /// <summary>When Claude's permission requests and questions wait for your phone instead of VS Code.</summary>
    public PhoneApprovals ApproveFromPhone { get; set; } = PhoneApprovals.WhenAway;

    /// <summary>No keyboard or mouse on this PC for this long counts as away (Helm's own typing does not count).</summary>
    public int AwayAfterMinutes { get; set; } = 2;

    /// <summary>How long a request waits for the phone before VS Code shows its own dialog.</summary>
    public int HoldMinutes { get; set; } = 10;

    /// <summary>A session nobody chose for is connected (the phone sees and steers it). Off: you connect each one.</summary>
    public bool ConnectNewSessions { get; set; } = true;

    /// <summary>Your choice per VS Code window (by folder, see <see cref="SessionRegistry.FolderKey"/>): all its sessions.</summary>
    public Dictionary<string, bool> WindowLinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Your choice per session (by id); wins over the window's.</summary>
    public Dictionary<string, bool> SessionLinks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Choices kept at most; the oldest sessions' go first (sessions are not reopened after days).</summary>
    public const int MaxSessionLinks = 400;

    /// <summary>Whether the phone may see and steer this session: the session's choice, else its window's, else the default.</summary>
    public bool IsConnected(string sessionId, string folder, IEnumerable<string> windowFolders)
    {
        if (SessionLinks.TryGetValue(sessionId, out var own)) return own;
        foreach (var root in windowFolders.OrderByDescending(f => f.Length))
        {
            if (!WorkspaceFolders.IsUnder(folder, root)) continue;
            if (WindowLinks.TryGetValue(SessionRegistry.FolderKey(root), out var window)) return window;
            break;
        }
        return ConnectNewSessions;
    }
}

public static class WorkspaceFolders
{
    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        var p = SessionRegistry.FolderKey(path);
        var r = SessionRegistry.FolderKey(root);
        return p == r || p.StartsWith(r + "\\", StringComparison.Ordinal);
    }
}
