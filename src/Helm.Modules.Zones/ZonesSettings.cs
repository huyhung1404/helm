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

    /// <summary>How close (px at 100% scale) the cursor must be to a zone for it to highlight when between zones.</summary>
    public int HighlightDistance { get; set; } = 20;

    // Layout switching
    /// <summary>Ctrl+Win+Alt+1…9 switches the monitor under the cursor to the layout with that number.</summary>
    public bool LayoutHotkeys { get; set; } = true;

    /// <summary>Briefly show the new layout's zones and name after switching.</summary>
    public bool FlashLayoutOnSwitch { get; set; } = true;

    // Window behavior
    /// <summary>Win+PgUp / Win+PgDn activates the previous/next window that shares the focused window's zone.</summary>
    public bool CycleWindowsInZone { get; set; } = true;

    public bool OverrideWindowsSnap { get; set; }
    public bool RestoreSizeOnUnsnap { get; set; } = true;
    public bool MoveNewWindowsToLastZone { get; set; }

    // Appearance
    public int ZoneOpacity { get; set; } = 50;
    public string ZoneColor { get; set; } = "#2B2B2B";
    public string BorderColor { get; set; } = "#FFFFFF";
    public string HighlightColor { get; set; } = "#0078D4";
    public bool ShowZoneNumbers { get; set; } = true;

    public List<string> ExcludedApps { get; set; } = [];

    public int SpanKey => ActivationKey == ZoneActivationKey.Ctrl ? VirtualKeyNames.Menu : VirtualKeyNames.Control;

    public int ActivationVirtualKey => ActivationKey switch
    {
        ZoneActivationKey.Ctrl => VirtualKeyNames.Control,
        ZoneActivationKey.Alt => VirtualKeyNames.Menu,
        _ => VirtualKeyNames.Shift,
    };
}
