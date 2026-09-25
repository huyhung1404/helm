using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Helm.Core.Hooks;

public sealed class KeyboardHookEventArgs : HookEventArgs
{
    public int VirtualKey { get; init; }
    public int ScanCode { get; init; }
    public bool IsKeyDown { get; init; }
    public bool IsSystemKey { get; init; }
}

/// <summary>Global keyboard hook (WH_KEYBOARD_LL). Shared singleton; modules <see cref="LowLevelHookBase{T}.Acquire"/> it.</summary>
public sealed class LowLevelKeyboardHook(ILogger<LowLevelKeyboardHook> logger)
    : LowLevelHookBase<KeyboardHookEventArgs>(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, "KeyboardHook", logger)
{
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    private protected override KeyboardHookEventArgs? CreateArgs(WPARAM wParam, LPARAM lParam)
    {
        var message = (uint)wParam.Value;
        if (message is not (WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)) return null;
        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        return new KeyboardHookEventArgs
        {
            VirtualKey = (int)data.vkCode,
            ScanCode = (int)data.scanCode,
            IsKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN,
            IsSystemKey = message is WM_SYSKEYDOWN or WM_SYSKEYUP,
            IsInjected = data.flags.HasFlag(KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED),
            ExtraInfo = data.dwExtraInfo,
            Time = data.time,
        };
    }
}
