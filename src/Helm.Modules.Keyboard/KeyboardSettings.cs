using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.Keyboard;

/// <summary>Which keys go round English → Vietnamese → Chinese.</summary>
public enum SwitchShortcut
{
    /// <summary>Left Ctrl and left Shift, pressed together and let go (as EVKey).</summary>
    LeftCtrlShift,
    /// <summary>Right Ctrl, pressed alone and let go.</summary>
    RightCtrl,
    /// <summary>Any shortcut, set in <see cref="KeyboardSettings.Hotkey"/>.</summary>
    Hotkey,
}

public sealed class KeyboardSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public static readonly HotkeyGesture DefaultHotkey = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'K');

    public int Version { get; set; }

    public SwitchShortcut Shortcut { get; set; } = SwitchShortcut.LeftCtrlShift;

    public HotkeyGesture Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Chinese is in the round (when a Chinese keyboard is installed).</summary>
    public bool IncludeChinese { get; set; } = true;

    /// <summary>EN / VI / 中 shows on screen for a moment after the shortcut.</summary>
    public bool ShowIndicator { get; set; } = true;

    public bool PlaySound { get; set; } = true;

    /// <summary>The tone mark on the second vowel of oa, oe, uy (hoà, thuý), as Vietnamese is written today.</summary>
    public bool ModernToneStyle { get; set; } = true;

    /// <summary>Words that cannot be Vietnamese stay as typed (windows, class).</summary>
    public bool SpellCheck { get; set; } = true;

    /// <summary>Apps Helm never switches: their keyboard stays as the user leaves it.</summary>
    public List<string> ExcludedApps { get; set; } = [];
}
