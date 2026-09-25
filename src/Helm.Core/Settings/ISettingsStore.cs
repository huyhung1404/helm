namespace Helm.Core.Settings;

/// <summary>A settings document with a schema version, so stores can migrate old files.</summary>
public interface IVersionedSettings
{
    /// <summary>The schema version this build writes.</summary>
    static abstract int CurrentVersion { get; }

    /// <summary>The schema version stored in the file.</summary>
    int Version { get; set; }

    /// <summary>Upgrades a document loaded from an older schema version in place.</summary>
    void Migrate(int fromVersion) { }
}

/// <summary>Loads and saves one typed JSON settings document, with defaults and debounced writes.</summary>
public interface ISettingsStore<T> where T : class, IVersionedSettings, new()
{
    string FilePath { get; }

    /// <summary>The live settings object. Mutate it through <see cref="Update"/> so the change is persisted and broadcast.</summary>
    T Current { get; }

    /// <summary>Raised after <see cref="Update"/> or <see cref="Reload"/>, on the thread that caused the change.</summary>
    event EventHandler<T>? Changed;

    /// <summary>Applies <paramref name="mutate"/>, raises <see cref="Changed"/> and schedules a debounced save.</summary>
    void Update(Action<T> mutate);

    /// <summary>Re-reads the file from disk (or defaults when missing).</summary>
    void Reload();

    /// <summary>Writes any pending change immediately.</summary>
    void Flush();
}

/// <summary>Hands out one shared store per settings file.</summary>
public interface ISettingsStoreFactory
{
    HelmPaths Paths { get; }

    /// <summary>Store for %LOCALAPPDATA%\Helm\settings\&lt;id&gt;.json.</summary>
    ISettingsStore<T> Get<T>(string id) where T : class, IVersionedSettings, new();

    /// <summary>Store for an arbitrary path relative to the settings directory, e.g. "zones/layouts.json".</summary>
    ISettingsStore<T> GetFile<T>(string relativePath) where T : class, IVersionedSettings, new();

    /// <summary>Writes all pending changes.</summary>
    void FlushAll();

    /// <summary>Stops all future writes (used by "Reset all settings" right before the app restarts).</summary>
    void SuspendWrites();
}
