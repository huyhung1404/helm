namespace Helm.Shell.Windows;

/// <summary>What Home and General need from the app's window and its tabs. Helm.App implements it (the tabs are its own).</summary>
public interface IShellWindowsHost
{
    /// <summary>Shows a page's tab (opening it if needed): a tool's module id, or home / general / bug-reports.</summary>
    void OpenPage(string key);

    /// <summary>Shows the General page.</summary>
    void OpenGeneral();

    /// <summary>Restarts Helm (after a reset).</summary>
    Task RestartAsync();
}
