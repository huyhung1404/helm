using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Core.Sync;

public enum SyncChangeOrigin
{
    /// <summary>Written on this device through the collection.</summary>
    Local,
    /// <summary>Arrived from another device (or is a conflict copy made while syncing).</summary>
    Remote,
}

public sealed class SyncedChangedEventArgs(IReadOnlyList<string> ids, SyncChangeOrigin origin) : EventArgs
{
    public IReadOnlyList<string> Ids { get; } = ids;
    public SyncChangeOrigin Origin { get; } = origin;
}

public sealed record SyncedItem<T>(string Id, T Value, DateTimeOffset UpdatedAt);

/// <summary>
/// A synced set of documents owned by one module. Reads and writes are local and immediate; the engine syncs in
/// the background. Records written by a newer Helm (higher schema) are invisible here and cannot be overwritten.
/// </summary>
public interface ISyncedCollection<T> where T : class
{
    string Name { get; }

    T? Get(string id);

    /// <summary>All live records, ordered by id.</summary>
    IReadOnlyList<SyncedItem<T>> All();

    /// <summary>Creates or replaces a record.</summary>
    /// <exception cref="InvalidOperationException">The stored record was written by a newer schema.</exception>
    void Upsert(string id, T value);

    /// <summary>Creates a record with a new time-sortable id and returns the id.</summary>
    string Add(T value);

    /// <returns>False when there was no live record with that id.</returns>
    bool Delete(string id);

    /// <summary>Raised after local writes (on the writing thread) and after syncs (on a thread-pool thread).</summary>
    event EventHandler<SyncedChangedEventArgs>? Changed;
}

public sealed class SyncedCollectionOptions<T> where T : class
{
    /// <summary>Stable collection name, e.g. "notes" or "chat.messages" (see <see cref="SyncIds.IsValidCollection"/>).</summary>
    public required string Name { get; init; }

    /// <summary>Bump when the JSON shape changes incompatibly, and handle the old shape in <see cref="Migrate"/>.</summary>
    public int SchemaVersion { get; init; } = 1;

    public SyncConflictPolicy ConflictPolicy { get; init; } = SyncConflictPolicy.LastWriterWins;

    /// <summary>KeepBoth only: adjusts the losing local edit before it is saved as a copy (e.g. add "(conflict)").</summary>
    public Func<T, T>? CreateConflictCopy { get; init; }

    /// <summary>
    /// Upgrades the JSON of a record stored with an older schema (the argument) to <see cref="SchemaVersion"/>.
    /// Applied on read only; the upgraded shape is written back the next time the record is edited.
    /// </summary>
    public Func<JsonNode, int, JsonNode>? Migrate { get; init; }

    internal SyncCollectionDescriptor ToDescriptor()
    {
        Func<string, string>? copy = null;
        if (CreateConflictCopy is { } create)
        {
            copy = body => JsonSerializer.Deserialize<T>(body, SyncJson.Options) is { } value
                ? JsonSerializer.Serialize(create(value), SyncJson.Options)
                : body;
        }
        return new SyncCollectionDescriptor(Name, SchemaVersion, ConflictPolicy, copy);
    }
}

internal static class SyncJson
{
    /// <summary>Record bodies as stored and synced.</summary>
    public static JsonSerializerOptions Options { get; } = new(HelmJson.Options) { WriteIndented = false };

    /// <summary>HTTP requests to the sync server: optional fields are omitted rather than sent as null.</summary>
    public static JsonSerializerOptions Wire { get; } = new(Options)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class SyncedCollection<T> : ISyncedCollection<T>, IDisposable where T : class
{
    private readonly SyncDatabase _db;
    private readonly SyncEngine _engine;
    private readonly SyncedCollectionOptions<T> _options;
    private readonly ILogger _logger;

    public SyncedCollection(SyncEngine engine, SyncedCollectionOptions<T> options, ILogger? logger = null)
    {
        _engine = engine;
        _db = engine.Database;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        engine.Register(options.ToDescriptor());
        engine.RecordsChanged += OnRecordsChanged;
    }

    public string Name => _options.Name;

    public event EventHandler<SyncedChangedEventArgs>? Changed;

    public T? Get(string id)
    {
        SyncIds.EnsureId(id);
        var row = _db.Get(Name, id);
        return row is null ? null : Read(row);
    }

    public IReadOnlyList<SyncedItem<T>> All()
    {
        var items = new List<SyncedItem<T>>();
        foreach (var row in _db.List(Name))
        {
            if (Read(row) is { } value)
                items.Add(new SyncedItem<T>(row.Id, value, DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtMs)));
        }
        return items;
    }

    public void Upsert(string id, T value)
    {
        SyncIds.EnsureId(id);
        ArgumentNullException.ThrowIfNull(value);
        var body = JsonSerializer.Serialize(value, SyncJson.Options);
        var result = _db.WriteLocal(Name, id, _options.SchemaVersion, body, deleted: false, Now());
        if (result == LocalWriteResult.RejectedNewerSchema)
            throw new InvalidOperationException($"{Name}/{id} was saved by a newer version of Helm; update Helm to edit it.");
        OnLocalWrite(id);
    }

    public string Add(T value)
    {
        var id = SyncIds.NewId(_engine.Time);
        Upsert(id, value);
        return id;
    }

    public bool Delete(string id)
    {
        SyncIds.EnsureId(id);
        var result = _db.WriteLocal(Name, id, _options.SchemaVersion, body: null, deleted: true, Now());
        if (result == LocalWriteResult.RejectedNewerSchema)
            throw new InvalidOperationException($"{Name}/{id} was saved by a newer version of Helm; update Helm to delete it.");
        if (result != LocalWriteResult.Written) return false;
        OnLocalWrite(id);
        return true;
    }

    public void Dispose() => _engine.RecordsChanged -= OnRecordsChanged;

    private T? Read(SyncRow row)
    {
        if (row.Deleted || row.Body is null || row.SchemaVersion > _options.SchemaVersion) return null;
        try
        {
            if (row.SchemaVersion < _options.SchemaVersion && _options.Migrate is { } migrate)
            {
                var node = JsonNode.Parse(row.Body) ?? throw new JsonException("Empty record body.");
                return migrate(node, row.SchemaVersion).Deserialize<T>(SyncJson.Options);
            }
            return JsonSerializer.Deserialize<T>(row.Body, SyncJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Synced data is untrusted input: a malformed record is skipped, never a crash.
            _logger.LogWarning(ex, "Skipping unreadable record {Collection}/{Id}", Name, row.Id);
            return null;
        }
    }

    private long Now() => _engine.Time.GetUtcNow().ToUnixTimeMilliseconds();

    private void OnLocalWrite(string id)
    {
        Changed?.Invoke(this, new SyncedChangedEventArgs([id], SyncChangeOrigin.Local));
        _engine.RequestSync();
    }

    private void OnRecordsChanged(object? sender, SyncRecordsChangedEventArgs e)
    {
        if (e.Collection == Name) Changed?.Invoke(this, new SyncedChangedEventArgs(e.Ids, SyncChangeOrigin.Remote));
    }
}
