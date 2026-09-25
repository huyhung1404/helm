namespace Helm.Core.Sync;

/// <summary>What happens when two devices edited the same record before seeing each other's change.</summary>
public enum SyncConflictPolicy
{
    /// <summary>The edit with the later timestamp wins (ties: higher device id). Right for settings-like data.</summary>
    LastWriterWins,

    /// <summary>
    /// The server's version keeps the id; the local edit is saved as a new record (a "conflict copy"), so no text is
    /// ever lost. An edit beats a deletion. Right for documents such as notes.
    /// </summary>
    KeepBoth,

    /// <summary>The server always wins. Right for append-only data whose records are never edited.</summary>
    RemoteWins,
}

/// <summary>
/// How the engine treats one collection. Registered for every synced collection so conflicts are resolved with the
/// right policy even before the module resolves its collection. Unknown collections use last-writer-wins.
/// </summary>
public sealed class SyncCollectionDescriptor
{
    public SyncCollectionDescriptor(string name, int schemaVersion, SyncConflictPolicy policy, Func<string, string>? createConflictCopy = null)
    {
        SyncIds.EnsureCollection(name);
        if (schemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        Name = name;
        SchemaVersion = schemaVersion;
        Policy = policy;
        CreateConflictCopy = createConflictCopy;
    }

    public string Name { get; }

    /// <summary>The highest schema this build understands; newer records are kept untouched and never overwritten.</summary>
    public int SchemaVersion { get; }

    public SyncConflictPolicy Policy { get; }

    /// <summary>KeepBoth only: turns the losing local body JSON into the body of its copy (e.g. rename the title).</summary>
    public Func<string, string>? CreateConflictCopy { get; }
}
