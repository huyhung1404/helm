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

    /// <summary>Local replica of synced data (see docs/sync-protocol.md).</summary>
    public string SyncDirectory => Path.Combine(Root, "sync");
    public string SyncDatabaseFile => Path.Combine(SyncDirectory, "helm-sync.db");
    public string SyncKeyFile => Path.Combine(SyncDirectory, "master.key");
    public string SyncCredentialsFile => Path.Combine(SyncDirectory, "credentials.bin");
    public string SyncLocalKeyFile => Path.Combine(SyncDirectory, "local.key");
}
