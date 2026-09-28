using System.Text.Json.Serialization;

namespace Helm.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A key combination such as Win+Ctrl+T. <see cref="Key"/> is a Win32 virtual-key code.</summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int Key)
{
    [JsonIgnore]
    public bool IsEmpty => Key == 0;

    public static HotkeyGesture None => default;

    /// <summary>Keycap labels in display order, e.g. ["Win", "Ctrl", "T"].</summary>
    public IReadOnlyList<string> ToKeycaps()
    {
        var caps = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) caps.Add("Win");
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) caps.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) caps.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) caps.Add("Shift");
        if (Key != 0) caps.Add(VirtualKeyNames.GetName(Key));
        return caps;
    }

    public override string ToString() => IsEmpty ? "(none)" : string.Join(" + ", ToKeycaps());
}

/// <summary>A hotkey a module owns: shown on Home and checked for conflicts.</summary>
public sealed record HotkeyDefinition(string ModuleId, string Id, string Description, HotkeyGesture Gesture)
{
    public string Key => $"{ModuleId}/{Id}";
}
