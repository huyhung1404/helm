using Helm.Core.Desktop;
using Helm.Core.Hotkeys;

namespace Helm.Tests;

public class HotkeyTests
{
    private static readonly HotkeyGesture WinCtrlT = new(HotkeyModifiers.Win | HotkeyModifiers.Ctrl, 'T');
    private static readonly HotkeyGesture WinShiftTick = new(HotkeyModifiers.Win | HotkeyModifiers.Shift, VirtualKeyNames.OemTilde);

    [Fact]
    public void No_conflicts_for_distinct_gestures()
    {
        var defs = new[]
        {
            new HotkeyDefinition("always-on-top", "toggle", "Pin", WinCtrlT),
            new HotkeyDefinition("zones", "editor", "Editor", WinShiftTick),
        };
        Assert.Empty(HotkeyConflictDetector.Detect(defs));
    }

    [Fact]
    public void Same_gesture_in_two_modules_is_a_conflict()
    {
        var defs = new[]
        {
            new HotkeyDefinition("always-on-top", "toggle", "Pin", WinCtrlT),
            new HotkeyDefinition("zones", "editor", "Editor", WinCtrlT),
        };

        var conflict = Assert.Single(HotkeyConflictDetector.Detect(defs));
        Assert.False(conflict.UsedByAnotherApp);
        Assert.Equal(2, conflict.Definitions.Count);
        Assert.Contains("Pin", conflict.Describe());
    }

    [Fact]
    public void Registration_failure_without_internal_clash_is_reported_as_external()
    {
        var def = new HotkeyDefinition("zones", "editor", "Editor", WinShiftTick);

        var conflict = Assert.Single(HotkeyConflictDetector.Detect([def], new HashSet<string> { def.Key }));

        Assert.True(conflict.UsedByAnotherApp);
    }

    [Fact]
    public void Internal_clash_is_not_double_reported_when_registration_also_failed()
    {
        var a = new HotkeyDefinition("a", "x", "A", WinCtrlT);
        var b = new HotkeyDefinition("b", "y", "B", WinCtrlT);

        var conflicts = HotkeyConflictDetector.Detect([a, b], new HashSet<string> { b.Key });

        Assert.Single(conflicts);
    }

    [Fact]
    public void Empty_gestures_are_ignored()
    {
        var defs = new[]
        {
            new HotkeyDefinition("a", "x", "A", HotkeyGesture.None),
            new HotkeyDefinition("b", "y", "B", HotkeyGesture.None),
        };
        Assert.Empty(HotkeyConflictDetector.Detect(defs));
    }

    [Fact]
    public void Keycaps_are_in_windows_order()
    {
        var gesture = new HotkeyGesture(HotkeyModifiers.Shift | HotkeyModifiers.Win | HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, VirtualKeyNames.OemTilde);
        Assert.Equal(["Win", "Ctrl", "Alt", "Shift", "`"], gesture.ToKeycaps());
        Assert.Equal("Win + Ctrl + T", WinCtrlT.ToString());
    }

    [Fact]
    public void Process_exclusions_match_with_or_without_extension()
    {
        var exclusions = new ProcessExclusions(ProcessExclusions.ParseLines("Unity.exe\r\n  devenv \n, unity.exe"));
        Assert.Equal(2, exclusions.Count);
        Assert.True(exclusions.IsExcluded("unity"));
        Assert.True(exclusions.IsExcluded("DEVENV.EXE"));
        Assert.False(exclusions.IsExcluded("Code.exe"));
        Assert.False(exclusions.IsExcluded(null));
    }
}
