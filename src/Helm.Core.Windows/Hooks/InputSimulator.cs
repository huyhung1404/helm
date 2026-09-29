using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Helm.Core.Hooks;

public static class InputSimulator
{
    /// <summary>dwExtraInfo stamped on input Helm injects, so our own hooks can ignore it ("HELM").</summary>
    public const uint HelmSignature = 0x48454C4D;

    private const ushort DummyKey = 0xFF; // unassigned VK; Windows ignores it but it "consumes" the Win key

    /// <summary>
    /// Sends a harmless key press. Call it after swallowing a Win+key combination so releasing Win does not open
    /// the Start menu.
    /// </summary>
    public static void SendDummyKey()
    {
        Span<INPUT> inputs =
        [
            Key(DummyKey, up: false),
            Key(DummyKey, up: true),
        ];
        PInvoke.SendInput(inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Types <paramref name="text"/> into the focused window as Unicode characters (any keyboard layout, any language),
    /// the way an input method does. Nothing passes through the clipboard.
    /// </summary>
    public static void TypeText(string text)
    {
        if (text.Length == 0) return;
        var inputs = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[2 * i] = Unicode(text[i], up: false);
            inputs[2 * i + 1] = Unicode(text[i], up: true);
        }
        PInvoke.SendInput(inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        Array.Clear(inputs); // the characters of a password
    }

    /// <summary>One press of a virtual key (VK_TAB = 0x09, VK_RETURN = 0x0D).</summary>
    public static void PressKey(ushort virtualKey)
    {
        Span<INPUT> inputs = [Key(virtualKey, up: false), Key(virtualKey, up: true)];
        PInvoke.SendInput(inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    /// <summary>True while Ctrl, Alt, Shift or a Win key is held (typing then would turn letters into shortcuts).</summary>
    public static bool AnyModifierDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0);

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT
            {
                wScan = c,
                dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0),
                dwExtraInfo = HelmSignature,
            },
        },
    };

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT
            {
                wVk = (VIRTUAL_KEY)vk,
                dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = HelmSignature,
            },
        },
    };
}
