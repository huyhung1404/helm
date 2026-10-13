using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Helm.Core.Desktop;

/// <summary>When someone last used this PC's keyboard or mouse (input Helm injects counts too; callers tell it apart).</summary>
public interface IUserActivity
{
    /// <summary>The time of the last keyboard or mouse input, on the <see cref="Environment.TickCount64"/> clock.</summary>
    long LastInputTick { get; }
}

public sealed class UserActivity : IUserActivity
{
    public long LastInputTick
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
            if (!PInvoke.GetLastInputInfo(ref info)) return Environment.TickCount64;
            // dwTime is GetTickCount's 32-bit clock: the time since that input, taken from the 32-bit now, is exact.
            var ago = unchecked((uint)Environment.TickCount - info.dwTime);
            return Environment.TickCount64 - ago;
        }
    }
}
