using Helm.Core.Hotkeys;
using Helm.Core.Settings;
using Helm.Modules.ClaudeChat.Cli;

namespace Helm.Modules.ClaudeChat;

/// <summary>
/// Device-local on purpose: paths and permission choices are about this machine and must never be synced.
/// </summary>
public sealed class ClaudeChatSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    /// <summary>Win+Alt+C: Win+Shift+C is PowerToys' Color Picker.</summary>
    public static readonly HotkeyGesture DefaultHotkey = new(HotkeyModifiers.Win | HotkeyModifiers.Alt, 'C');

    public int Version { get; set; }

    /// <summary>Shows the chat: its floating window when torn out, otherwise the Claude Chat page.</summary>
    public HotkeyGesture Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Size of the floating chat window, in device-independent pixels (kept across tear-outs).</summary>
    public double FloatingWidth { get; set; } = 560;

    public double FloatingHeight { get; set; } = 760;

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
