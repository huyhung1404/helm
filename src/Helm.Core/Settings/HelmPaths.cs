namespace Helm.Core.Settings;

/// <summary>Well-known locations under %LOCALAPPDATA%\Helm.</summary>
public sealed class HelmPaths
{
    public HelmPaths(string? root = null)
    {
        Root = root ?? DefaultRoot;
    }

    /// <summary>Where Helm keeps its data unless it is started with --data-dir.</summary>
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Helm");

    public string Root { get; }

    /// <summary>
    /// False for a copy of Helm on a test data folder (--data-dir): such a copy leaves files of other programs that
    /// belong to the user (Claude Code's settings) alone, so it never takes the installed Helm's place there.
    /// </summary>
    public bool IsDefault => string.Equals(Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(DefaultRoot).TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Encrypted chunks of synced files (blobs): uploads waiting to be sent, and a cache of downloaded ones.</summary>
    public string SyncBlobsDirectory => Path.Combine(SyncDirectory, "blobs");
}
