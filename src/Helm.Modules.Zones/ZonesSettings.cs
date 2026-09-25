using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.Zones;

public enum ZoneActivationKey
{
    Shift,
    Ctrl,
    Alt,
}

public sealed class ZonesSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public static readonly HotkeyGesture DefaultEditorHotkey = new(HotkeyModifiers.Win | HotkeyModifiers.Shift, VirtualKeyNames.OemTilde);

    public int Version { get; set; }

    // Activation
    public ZoneActivationKey ActivationKey { get; set; } = ZoneActivationKey.Shift;

    /// <summary>Show zones on every window drag instead of only while the activation key is held.</summary>
    public bool AlwaysShowZones { get; set; }

    /// <summary>Hold the span key (Ctrl, or Alt when Ctrl activates zones) while dragging to select several zones.</summary>
    public bool MultiZoneSpanning { get; set; } = true;

    // Editor
    public HotkeyGesture EditorHotkey { get; set; } = DefaultEditorHotkey;

    // Window behavior
    public bool OverrideWindowsSnap { get; set; }
    public bool RestoreSizeOnUnsnap { get; set; } = true;
    public bool MoveNewWindowsToLastZone { get; set; }

    // Appearance
    public int ZoneOpacity { get; set; } = 50;
    public string ZoneColor { get; set; } = "#2B2B2B";
    public string BorderColor { get; set; } = "#FFFFFF";
    public string HighlightColor { get; set; } = "#0078D4";
    public bool ShowZoneNumbers { get; set; } = true;
    public int PickerColumns { get; set; } = 3;

    public List<string> ExcludedApps { get; set; } = [];

    public int SpanKey => ActivationKey == ZoneActivationKey.Ctrl ? VirtualKeyNames.Menu : VirtualKeyNames.Control;

    public int ActivationVirtualKey => ActivationKey switch
    {
        ZoneActivationKey.Ctrl => VirtualKeyNames.Control,
        ZoneActivationKey.Alt => VirtualKeyNames.Menu,
        _ => VirtualKeyNames.Shift,
    };
}
