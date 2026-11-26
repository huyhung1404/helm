using System.ComponentModel;

namespace Helm.Shell.Home;

/// <summary>
/// What only the platform answers in General: the rows that are its own (theme, startup, administrator mode), the
/// folders, and reset. General shows them and writes them back; a change on either side raises <c>PropertyChanged</c>.
/// </summary>
public interface IGeneralPlatform : INotifyPropertyChanged
{
    /// <summary>The app's settings folder; empty where there is none to open (a phone).</summary>
    string SettingsFolder { get; }

    /// <summary>0 = like the system, 1 = light, 2 = dark.</summary>
    int ThemeIndex { get; set; }

    /// <summary>Starts with Windows (a desktop only).</summary>
    bool RunAtStartup { get; set; }

    /// <summary>Starts in the tray (a desktop only).</summary>
    bool StartMinimized { get; set; }

    /// <summary>Helm is running as administrator now (shown, not changed).</summary>
    bool RunAsAdministrator { get; }

    void OpenSettingsFolder();

    void OpenLogs();

    Task ResetAllAsync();
}
