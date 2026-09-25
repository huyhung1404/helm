namespace Helm.Core.Sync;

/// <summary>
/// Append-only synced data (chat messages, history). Entries get time-sortable ids and are never edited, so two
/// devices can never conflict; <see cref="All"/> returns them in creation order.
/// </summary>
public interface ISyncedLog<T> where T : class
{
    string Name { get; }

    /// <returns>The new entry's id.</returns>
    string Append(T entry);

    /// <summary>Entries in creation order (to the millisecond; ties in random order).</summary>
    IReadOnlyList<SyncedItem<T>> All();

    /// <summary>Removes an entry, e.g. to prune old history.</summary>
    bool Remove(string id);

    event EventHandler<SyncedChangedEventArgs>? Changed;
}

public sealed class SyncedLog<T> : ISyncedLog<T>, IDisposable where T : class
{
    private readonly SyncedCollection<T> _inner;

    public SyncedLog(SyncEngine engine, string name, int schemaVersion = 1)
    {
        _inner = new SyncedCollection<T>(engine, Options(name, schemaVersion));
    }

    public string Name => _inner.Name;

    public event EventHandler<SyncedChangedEventArgs>? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public string Append(T entry) => _inner.Add(entry);

    public IReadOnlyList<SyncedItem<T>> All() => _inner.All();

    public bool Remove(string id) => _inner.Delete(id);

    public void Dispose() => _inner.Dispose();

    internal static SyncedCollectionOptions<T> Options(string name, int schemaVersion) => new()
    {
        Name = name,
        SchemaVersion = schemaVersion,
        ConflictPolicy = SyncConflictPolicy.RemoteWins,
    };
}
