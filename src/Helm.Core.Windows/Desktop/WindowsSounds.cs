using System.Runtime.InteropServices;

namespace Helm.Core.Desktop;

/// <summary>Windows' own short sounds (the sound scheme the user picked), without WPF or WinForms.</summary>
public static class WindowsSounds
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconAsterisk = 0x00000040;

    /// <summary>The default beep.</summary>
    public static void Beep() => MessageBeep(MbOk);

    /// <summary>The "asterisk" (information) sound.</summary>
    public static void Asterisk() => MessageBeep(MbIconAsterisk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);
}
