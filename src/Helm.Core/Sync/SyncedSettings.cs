using System.Text.Json;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Core.Sync;

/// <summary>
/// Settings shared by every device, with the same shape as <see cref="ISettingsStore{T}"/>. Keep device-specific
/// values (window placement, monitor layouts, paths, anything that launches a program) in a normal settings store:
/// synced values are applied on every device and must be safe to receive from any of them.
/// Conflicts: the whole document is last-writer-wins.
/// </summary>
public interface ISyncedSettings<T> where T : class, IVersionedSettings, new()
{
    T Current { get; }

    /// <summary>Raised after <see cref="Update"/> and after another device's change arrives.</summary>
    event EventHandler<T>? Changed;

    /// <summary>Applies <paramref name="mutate"/>, raises <see cref="Changed"/> and schedules a debounced save.</summary>
    void Update(Action<T> mutate);

    /// <summary>Writes any pending change immediately.</summary>
    void Flush();
}

public sealed class SyncedSettings<T> : ISyncedSettings<T>, IDisposable where T : class, IVersionedSettings, new()
{
    internal const string DocumentId = "doc";
    private readonly object _gate = new();
    private readonly SyncDatabase _db;
    private readonly SyncEngine _engine;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly Timer _timer;
    private bool _dirty;
    private bool _readOnly;

    public SyncedSettings(SyncEngine engine, string storeId, ILogger? logger = null, TimeSpan? debounce = null)
    {
        Collection = CollectionName(storeId);
        _engine = engine;
        _db = engine.Database;
        _logger = logger ?? NullLogger.Instance;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(400);
        _timer = new Timer(_ => FlushFromTimer(), null, Timeout.Infinite, Timeout.Infinite);
        engine.Register(Descriptor(storeId));
        Current = Load();
        engine.RecordsChanged += OnRecordsChanged;
    }

    public string Collection { get; }

    public T Current { get; private set; }

    public event EventHandler<T>? Changed;

    internal static string CollectionName(string storeId) => "settings." + storeId.ToLowerInvariant();

    internal static SyncCollectionDescriptor Descriptor(string storeId) =>
        new(CollectionName(storeId), T.CurrentVersion, SyncConflictPolicy.LastWriterWins);

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

    public void Flush()
    {
        string body;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            if (_readOnly)
            {
                _logger.LogWarning("{Collection} was saved by a newer Helm; local changes are not synced", Collection);
                return;
            }
            Current.Version = T.CurrentVersion;
            body = JsonSerializer.Serialize(Current, SyncJson.Options);
        }

        var result = _db.WriteLocal(Collection, DocumentId, T.CurrentVersion, body, deleted: false,
            _engine.Time.GetUtcNow().ToUnixTimeMilliseconds());
        if (result == LocalWriteResult.RejectedNewerSchema)
        {
            lock (_gate) _readOnly = true;
            _logger.LogWarning("{Collection} was saved by a newer Helm; local changes are not synced", Collection);
            return;
        }
        _engine.RequestSync();
    }

    public void Dispose()
    {
        _engine.RecordsChanged -= OnRecordsChanged;
        Flush();
        _timer.Dispose();
    }

    private T Load()
    {
        var row = _db.Get(Collection, DocumentId);
        if (row is null || row.Deleted || row.Body is null) return new T { Version = T.CurrentVersion };
        if (row.SchemaVersion > T.CurrentVersion)
        {
            _readOnly = true;
            return new T { Version = T.CurrentVersion };
        }
        _readOnly = false;
        try
        {
            var loaded = JsonSerializer.Deserialize<T>(row.Body, SyncJson.Options) ?? new T();
            if (loaded.Version < T.CurrentVersion)
            {
                loaded.Migrate(loaded.Version);
                loaded.Version = T.CurrentVersion;
            }
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Synced settings {Collection} are unreadable; using defaults", Collection);
            return new T { Version = T.CurrentVersion };
        }
    }

    private void OnRecordsChanged(object? sender, SyncRecordsChangedEventArgs e)
    {
        if (e.Collection != Collection) return;
        lock (_gate)
        {
            // A pending local change is newer; it will be written and win (or lose) on the next sync.
            if (_dirty) return;
            Current = Load();
        }
        Changed?.Invoke(this, Current);
    }

    private void FlushFromTimer()
    {
        try
        {
            Flush();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Debounced synced-settings write failed for {Collection}", Collection);
        }
    }
}
