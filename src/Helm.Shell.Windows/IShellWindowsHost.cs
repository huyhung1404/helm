namespace Helm.Shell.Windows;

/// <summary>What Home and General need from the app's window and its tabs. Helm.App implements it (the tabs are its own).</summary>
public interface IShellWindowsHost
{
    /// <summary>Shows a page's tab (opening it if needed), e.g. a tool's content or settings page.</summary>
    void OpenPage(Type pageType);

    /// <summary>Shows the General page.</summary>
    void OpenGeneral();

    void OpenWhatsNew();

    /// <summary>Restarts Helm (after a reset).</summary>
    Task RestartAsync();
}
