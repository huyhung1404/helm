using Helm.Core.Geometry;
using Microsoft.Win32;

namespace Helm.Core.Desktop;

public static class AccentColor
{
    private static readonly RgbColor s_fallback = new(0, 120, 212);

    /// <summary>The Windows accent color (HKCU\Software\Microsoft\Windows\DWM\AccentColor, stored as ABGR).</summary>
    public static RgbColor Current
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key?.GetValue("AccentColor") is int abgr)
                {
                    var v = unchecked((uint)abgr);
                    return new RgbColor((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            return s_fallback;
        }
    }
}
