using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.WindowZones;

/// <summary>The key held while dragging a window to show the zones and drop it into one.</summary>
public enum ZoneDragKey
{
    Shift,
    Ctrl,
    Alt,
}

public sealed class WindowZonesSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public static readonly HotkeyModifiers DefaultZoneModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt;
    public static readonly HotkeyGesture DefaultNextWindow = new(HotkeyModifiers.Win, VirtualKeyNames.PageDown);
    public static readonly HotkeyGesture DefaultPreviousWindow = new(HotkeyModifiers.Win, VirtualKeyNames.PageUp);
    public static readonly HotkeyGesture DefaultEditorHotkey = new(HotkeyModifiers.Win | HotkeyModifiers.Shift, VirtualKeyNames.OemTilde);

    /// <summary>The choices offered for <see cref="ZoneModifiers"/> (each plus 1…9). None of them is taken by Windows.</summary>
    public static readonly IReadOnlyList<HotkeyModifiers> ZoneModifierChoices =
    [
        HotkeyModifiers.Ctrl | HotkeyModifiers.Alt,
        HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
        HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win,
    ];

    public const int MinOpacity = 20, MaxOpacity = 90;

    public int Version { get; set; }

    // Shortcuts
    /// <summary>Held with a zone's number (1…9) to open or close that zone.</summary>
    public HotkeyModifiers ZoneModifiers { get; set; } = DefaultZoneModifiers;

    /// <summary>Next window in the zone of the window in front. Only taken while that zone has two windows or more.</summary>
    public HotkeyGesture NextWindowHotkey { get; set; } = DefaultNextWindow;

    public HotkeyGesture PreviousWindowHotkey { get; set; } = DefaultPreviousWindow;

    public HotkeyGesture EditorHotkey { get; set; } = DefaultEditorHotkey;

    // Behavior
    public ZoneDragKey DragKey { get; set; } = ZoneDragKey.Shift;

    /// <summary>A window dragged out of its zone gets back the size it had before it went in.</summary>
    public bool RestoreSizeOnLeave { get; set; } = true;

    /// <summary>Show the zone's windows as tabs for a moment when switching between them or opening the zone.</summary>
    public bool ShowTabStrip { get; set; } = true;

    // Appearance
    /// <summary>Opacity (%) of the zones shown while dragging.</summary>
    public int ZoneOpacity { get; set; } = 55;

    public bool ShowZoneNumbers { get; set; } = true;

    public List<string> ExcludedApps { get; set; } = [];

    public int DragVirtualKey => DragKey switch
    {
        ZoneDragKey.Ctrl => VirtualKeyNames.Control,
        ZoneDragKey.Alt => VirtualKeyNames.Menu,
        _ => VirtualKeyNames.Shift,
    };

    public HotkeyModifiers DragModifier => DragKey switch
    {
        ZoneDragKey.Ctrl => HotkeyModifiers.Ctrl,
        ZoneDragKey.Alt => HotkeyModifiers.Alt,
        _ => HotkeyModifiers.Shift,
    };

    /// <summary>The shortcut of zone <paramref name="number"/>, e.g. Ctrl+Alt+2.</summary>
    public HotkeyGesture ZoneHotkey(int number) => new(ZoneModifiers, '0' + number);
}
