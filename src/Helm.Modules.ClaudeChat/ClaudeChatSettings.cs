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

    /// <summary>Size of the chat window, in device-independent pixels.</summary>
    public double FloatingWidth { get; set; } = 960;

    public double FloatingHeight { get; set; } = 760;

    /// <summary>Where the chat window was last closed (DIPs); null = centred on the screen.</summary>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    /// <summary>The folders open in the chat window's tree, in order (restored next time).</summary>
    public List<string> OpenFolders { get; set; } = [];

    /// <summary>Chats open in the tree that have started (so they can be resumed); empty new chats are not kept.</summary>
    public List<OpenChat> OpenChats { get; set; } = [];

    /// <summary>The folder Claude Code works in (its project). Empty means the user's profile folder.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>A model alias or id passed to <c>--model</c>; empty uses the CLI's own default.</summary>
    public string Model { get; set; } = string.Empty;

    public ClaudePermissionMode PermissionMode { get; set; } = ClaudePermissionMode.Manual;

    /// <summary>Full path to claude.exe / claude.cmd; empty finds it on PATH.</summary>
    public string CliPath { get; set; } = string.Empty;

    /// <summary>One chat of the tree, remembered by its Claude Code session id.</summary>
    public sealed record OpenChat(string Folder, string SessionId);

    public string ResolveWorkingDirectory() =>
        string.IsNullOrWhiteSpace(WorkingDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : WorkingDirectory.Trim();
}
