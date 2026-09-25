namespace Helm.Core.Settings;

/// <summary>Well-known locations under %LOCALAPPDATA%\Helm.</summary>
public sealed class HelmPaths
{
    public HelmPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Helm");
    }

    public string Root { get; }
    public string SettingsDirectory => Path.Combine(Root, "settings");
    public string LogsDirectory => Path.Combine(Root, "logs");

    public string SettingsFile(string id) => Path.Combine(SettingsDirectory, $"{id}.json");

    public string ModuleDataDirectory(string moduleId) => Path.Combine(SettingsDirectory, moduleId);
}
