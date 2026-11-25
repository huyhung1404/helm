namespace Helm.Shell.Home;

/// <summary>Where a tap on Home or on an alert goes. Each platform answers it with its own navigation.</summary>
public interface IHomeLinks
{
    /// <summary>Opens a tool's page.</summary>
    void OpenTool(string moduleId);

    /// <summary>Opens General at one of its parts: "tools", "sync", "updates", "appearance", "background", "help".</summary>
    void OpenGeneral(string part);

    void OpenWhatsNew();
}
