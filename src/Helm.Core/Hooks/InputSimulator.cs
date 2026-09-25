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
