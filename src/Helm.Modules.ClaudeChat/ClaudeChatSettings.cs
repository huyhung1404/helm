using Helm.Core.Settings;
using Helm.Modules.ClaudeChat.Cli;

namespace Helm.Modules.ClaudeChat;

/// <summary>
/// Device-local on purpose: paths and permission choices are about this machine and must never be synced.
/// </summary>
public sealed class ClaudeChatSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>The folder Claude Code works in (its project). Empty means the user's profile folder.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>A model alias or id passed to <c>--model</c>; empty uses the CLI's own default.</summary>
    public string Model { get; set; } = string.Empty;

    public ClaudePermissionMode PermissionMode { get; set; } = ClaudePermissionMode.Manual;

    /// <summary>Full path to claude.exe / claude.cmd; empty finds it on PATH.</summary>
    public string CliPath { get; set; } = string.Empty;

    public string ResolveWorkingDirectory() =>
        string.IsNullOrWhiteSpace(WorkingDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : WorkingDirectory.Trim();
}
