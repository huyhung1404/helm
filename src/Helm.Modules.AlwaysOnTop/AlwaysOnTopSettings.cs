using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.AlwaysOnTop;

public sealed class AlwaysOnTopSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    public static readonly HotkeyGesture DefaultHotkey = new(HotkeyModifiers.Win | HotkeyModifiers.Ctrl, 'T');

    public int Version { get; set; }

    public HotkeyGesture Hotkey { get; set; } = DefaultHotkey;

    public bool ShowBorder { get; set; } = true;

    /// <summary>Use the Windows accent color instead of <see cref="BorderColor"/>.</summary>
    public bool UseAccentColor { get; set; } = true;

    public string BorderColor { get; set; } = "#0078D4";

    public int BorderThickness { get; set; } = 3;

    public bool PlaySound { get; set; } = true;

    /// <summary>Ignore the hotkey while a full-screen game (Direct3D exclusive) or presentation is running.</summary>
    public bool DoNotActivateInGameMode { get; set; } = true;

    public List<string> ExcludedApps { get; set; } = [];
}
