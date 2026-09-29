using Helm.Core.Hotkeys;
using Helm.Core.Settings;

namespace Helm.Modules.QuickCapture;

public sealed class QuickCaptureSettings : IVersionedSettings
{
    public static int CurrentVersion => 1;

    /// <summary>Win+Alt+N: free in Windows, and N for "note".</summary>
    public static readonly HotkeyGesture DefaultHotkey = new(HotkeyModifiers.Win | HotkeyModifiers.Alt, 'N');

    public int Version { get; set; }

    public HotkeyGesture Hotkey { get; set; } = DefaultHotkey;

    /// <summary>The target selected when the box opens empty (a target id such as "note" or "task").</summary>
    public string DefaultTarget { get; set; } = "note";
}
