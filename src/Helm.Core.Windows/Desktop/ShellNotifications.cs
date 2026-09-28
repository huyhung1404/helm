using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Helm.Core.Desktop;

public static class ShellNotifications
{
    /// <summary>
    /// Tells Explorer that file associations/icons changed, so shortcuts and the taskbar drop cached icons.
    /// Needed after an update: the shortcut path stays the same while the icon inside Helm.exe changed.
    /// </summary>
    public static void RefreshIcons()
    {
        unsafe
        {
            PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null);
        }
    }
}
