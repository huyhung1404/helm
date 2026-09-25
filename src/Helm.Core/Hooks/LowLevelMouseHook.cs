using System.Runtime.InteropServices;
using Helm.Core.Geometry;
using Microsoft.Extensions.Logging;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Helm.Core.Hooks;

public enum MouseMessage
{
    Move,
    LeftDown,
    LeftUp,
    RightDown,
    RightUp,
    MiddleDown,
    MiddleUp,
    Wheel,
    Other,
}

public sealed class MouseHookEventArgs : HookEventArgs
{
    public MouseMessage Message { get; init; }
    public PixelPoint Position { get; init; }
    public int WheelDelta { get; init; }
}

/// <summary>Global mouse hook (WH_MOUSE_LL). Shared singleton; modules <see cref="LowLevelHookBase{T}.Acquire"/> it.</summary>
public sealed class LowLevelMouseHook(ILogger<LowLevelMouseHook> logger)
    : LowLevelHookBase<MouseHookEventArgs>(WINDOWS_HOOK_ID.WH_MOUSE_LL, "MouseHook", logger)
{
    private const uint LLMHF_INJECTED = 0x1;

    private protected override MouseHookEventArgs? CreateArgs(WPARAM wParam, LPARAM lParam)
    {
        var message = (uint)wParam.Value switch
        {
            0x0200 => MouseMessage.Move,
            0x0201 => MouseMessage.LeftDown,
            0x0202 => MouseMessage.LeftUp,
            0x0204 => MouseMessage.RightDown,
            0x0205 => MouseMessage.RightUp,
            0x0207 => MouseMessage.MiddleDown,
            0x0208 => MouseMessage.MiddleUp,
            0x020A => MouseMessage.Wheel,
            _ => MouseMessage.Other,
        };
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        return new MouseHookEventArgs
        {
            Message = message,
            Position = new PixelPoint(data.pt.X, data.pt.Y),
            WheelDelta = message == MouseMessage.Wheel ? (short)(data.mouseData >> 16) : 0,
            IsInjected = (data.flags & LLMHF_INJECTED) != 0,
            ExtraInfo = data.dwExtraInfo,
            Time = data.time,
        };
    }
}
