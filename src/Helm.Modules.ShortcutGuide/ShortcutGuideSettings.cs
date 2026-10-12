using System.Text.Json.Serialization;
using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.ShortcutGuide;

/// <summary>Where the guide appears on the monitor of the app in front.</summary>
public enum GuidePosition
{
    Center,
    Bottom,
}

/// <summary>An app you added yourself: a name and the processes it runs as ("blender" or "blender.exe").</summary>
public sealed class UserApp
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> Processes { get; set; } = [];
}

/// <summary>A shortcut you added to any app, built-in or your own. It replaces a built-in one with the same keys.</summary>
public sealed class UserShortcut
{
    public string AppId { get; set; } = string.Empty;

    public string Keys { get; set; } = string.Empty;

    public string Does { get; set; } = string.Empty;
}

public sealed class ShortcutGuideSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public const int MinHoldDelayMs = 300, MaxHoldDelayMs = 2000, DefaultHoldDelayMs = 700;

    public static readonly IReadOnlyList<string> PositionNames = ["Center", "Bottom"];

    public int Version { get; set; }

    public bool HoldCtrl { get; set; } = true;

    public bool HoldAlt { get; set; } = true;

    public bool HoldWin { get; set; } = true;

    /// <summary>Off by default: Shift is held while typing capitals, and few apps have Shift-only shortcuts.</summary>
    public bool HoldShift { get; set; }

    /// <summary>How long a modifier must be held, alone, before the guide appears.</summary>
    public int HoldDelayMs { get; set; } = DefaultHoldDelayMs;

    public GuidePosition Position { get; set; } = GuidePosition.Center;

    /// <summary>Also list the shortcuts most apps share (copy, paste, undo …) and those of Windows itself.</summary>
    public bool IncludeGeneral { get; set; } = true;

    /// <summary>Never show the guide over a full-screen game or a presentation.</summary>
    public bool HideInFullScreen { get; set; } = true;

    /// <summary>Process names the guide never shows over (games, remote desktops, apps with their own guide).</summary>
    public List<string> ExcludedApps { get; set; } = [];

    public List<UserApp> Apps { get; set; } = [];

    public List<UserShortcut> Shortcuts { get; set; } = [];

    /// <summary>Built-in shortcuts you hid, as "appId|Canonical keys" (<see cref="ShortcutLibrary.HiddenKey"/>).</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>The modifiers that open the guide when held.</summary>
    [JsonIgnore]
    public HotkeyModifiers HoldModifiers =>
        (HoldCtrl ? HotkeyModifiers.Ctrl : 0) | (HoldAlt ? HotkeyModifiers.Alt : 0) |
        (HoldWin ? HotkeyModifiers.Win : 0) | (HoldShift ? HotkeyModifiers.Shift : 0);
}
