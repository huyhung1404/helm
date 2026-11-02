namespace Helm.Modules.Keyboard;

/// <summary>
/// Windows that control another computer (Chrome Remote Desktop, Remote Desktop, UltraViewer…). In them the switch
/// shortcut is the other computer's: its Helm switches its keyboard, and this computer stays on English, as letters are
/// sent on as keys there.
/// </summary>
public static class RemoteWindows
{
    private const string ChromeRemoteDesktop = "remotedesktop.google.com";

    private static readonly string[] s_apps =
        ["mstsc", "msrdc", "UltraViewer_Desktop", "UltraViewer", "TeamViewer", "AnyDesk", "rustdesk", "parsecd", "vncviewer"];

    /// <summary>The program controls another computer ("mstsc.exe", "UltraViewer_Desktop.exe"…).</summary>
    public static bool IsRemoteApp(string? process)
    {
        if (string.IsNullOrWhiteSpace(process)) return false;
        var name = process.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return s_apps.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The browser page is a Chrome Remote Desktop session ("remotedesktop.google.com/access/session/…"), as its address
    /// bar shows it (with or without https://); its list of computers is not.
    /// </summary>
    public static bool IsRemoteAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var text = address.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];
        var slash = text.IndexOf('/');
        if (slash < 0) return false;
        return text[..slash].Equals(ChromeRemoteDesktop, StringComparison.OrdinalIgnoreCase)
            && text[slash..].Contains("/session", StringComparison.OrdinalIgnoreCase);
    }
}
