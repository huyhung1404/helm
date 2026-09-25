using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Core.Settings;

public static class HelmJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public sealed class SettingsStore<T> : ISettingsStore<T>, IDisposable where T : class, IVersionedSettings, new()
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly Func<bool> _writesSuspended;
    private readonly Timer _timer;
    private bool _dirty;

    public SettingsStore(string filePath, ILogger? logger = null, TimeSpan? debounce = null, Func<bool>? writesSuspended = null)
    {
        FilePath = filePath;
        _logger = logger ?? NullLogger.Instance;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(400);
        _writesSuspended = writesSuspended ?? (() => false);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Current = Load();
    }

    public string FilePath { get; }

    public T Current { get; private set; }

    public event EventHandler<T>? Changed;

    public void Update(Action<T> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            _dirty = true;
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke(this, Current);
    }

    public void Reload()
    {
        lock (_gate)
        {
            _dirty = false;
            Current = Load();
        }
        Changed?.Invoke(this, Current);
    }

    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            if (_writesSuspended()) return;
            Current.Version = T.CurrentVersion;
            json = JsonSerializer.Serialize(Current, HelmJson.Options);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write settings {File}", FilePath);
        }
    }

    public void Dispose()
    {
        Flush();
        _timer.Dispose();
    }

    private T Load()
    {
        if (!File.Exists(FilePath)) return new T { Version = T.CurrentVersion };
        try
        {
            var loaded = JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath), HelmJson.Options) ?? new T();
            if (loaded.Version < T.CurrentVersion)
            {
                _logger.LogInformation("Migrating {File} from v{From} to v{To}", FilePath, loaded.Version, T.CurrentVersion);
                loaded.Migrate(loaded.Version);
                loaded.Version = T.CurrentVersion;
                _dirty = true;
            }
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Settings file {File} is unreadable; using defaults", FilePath);
            TryBackupCorruptFile();
            return new T { Version = T.CurrentVersion };
        }
    }

    private void TryBackupCorruptFile()
    {
        try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class SettingsStoreFactory(HelmPaths paths, ILoggerFactory? loggerFactory = null) : ISettingsStoreFactory, IDisposable
{
    private readonly Dictionary<string, object> _stores = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _suspended;

    public HelmPaths Paths { get; } = paths;

    public ISettingsStore<T> Get<T>(string id) where T : class, IVersionedSettings, new() =>
        GetOrCreate<T>(Paths.SettingsFile(id));

    public ISettingsStore<T> GetFile<T>(string relativePath) where T : class, IVersionedSettings, new() =>
        GetOrCreate<T>(Path.Combine(Paths.SettingsDirectory, relativePath));

    public void FlushAll()
    {
        foreach (var store in Snapshot()) ((dynamic)store).Flush();
    }

    public void SuspendWrites() => _suspended = true;

    public void Dispose()
    {
        foreach (var store in Snapshot())
            if (store is IDisposable d) d.Dispose();
    }

    private ISettingsStore<T> GetOrCreate<T>(string path) where T : class, IVersionedSettings, new()
    {
        lock (_stores)
        {
            if (_stores.TryGetValue(path, out var existing)) return (ISettingsStore<T>)existing;
            var logger = loggerFactory?.CreateLogger($"Settings.{typeof(T).Name}");
            var store = new SettingsStore<T>(path, logger, writesSuspended: () => _suspended);
            _stores[path] = store;
            return store;
        }
    }

    private List<object> Snapshot()
    {
        lock (_stores) return _stores.Values.ToList();
    }
}
