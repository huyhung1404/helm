using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.CommandPalette;

public sealed class CommandPaletteSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    /// <summary>Alt+Space, the usual launcher shortcut (it replaces the window menu that Alt+Space opens).</summary>
    public static readonly HotkeyGesture DefaultHotkey = new(HotkeyModifiers.Alt, 0x20);

    public int Version { get; set; }

    public HotkeyGesture Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Also find the apps in the Start menu and open them.</summary>
    public bool IncludeApps { get; set; } = true;
}
