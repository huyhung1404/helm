namespace Helm.Core.Hotkeys;

/// <summary>Human readable names for Win32 virtual-key codes.</summary>
public static class VirtualKeyNames
{
    public const int Back = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Control = 0x11, Menu = 0x12, Pause = 0x13,
        CapsLock = 0x14, Escape = 0x1B, Space = 0x20, PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24,
        Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, PrintScreen = 0x2C, Insert = 0x2D, Delete = 0x2E,
        LWin = 0x5B, RWin = 0x5C, Apps = 0x5D, F1 = 0x70, F24 = 0x87, LShift = 0xA0, RShift = 0xA1,
        LControl = 0xA2, RControl = 0xA3, LMenu = 0xA4, RMenu = 0xA5, OemTilde = 0xC0;

    private static readonly Dictionary<int, string> s_names = new()
    {
        [Back] = "Backspace", [Tab] = "Tab", [Enter] = "Enter", [Pause] = "Pause", [CapsLock] = "Caps Lock",
        [Escape] = "Esc", [Space] = "Space", [PageUp] = "PgUp", [PageDown] = "PgDn", [End] = "End", [Home] = "Home",
        [Left] = "←", [Up] = "↑", [Right] = "→", [Down] = "↓", [PrintScreen] = "PrtSc", [Insert] = "Ins",
        [Delete] = "Del", [Apps] = "Menu",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/", [OemTilde] = "`",
        [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
        [0x6A] = "Num *", [0x6B] = "Num +", [0x6D] = "Num -", [0x6E] = "Num .", [0x6F] = "Num /",
    };

    public static bool IsModifier(int vk) =>
        vk is Shift or Control or Menu or LWin or RWin or LShift or RShift or LControl or RControl or LMenu or RMenu;

    public static string GetName(int vk)
    {
        if (s_names.TryGetValue(vk, out var name)) return name;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= F1 and <= F24) return $"F{vk - F1 + 1}";
        if (vk is >= 0x60 and <= 0x69) return $"Num {vk - 0x60}";
        return $"0x{vk:X2}";
    }
}
