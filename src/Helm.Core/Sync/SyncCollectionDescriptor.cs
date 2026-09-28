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
    public SyncCollectionDescriptor(string name, int schemaVersion, SyncConflictPolicy policy, Func<string, string>? createConflictCopy = null,
        SyncChangeGuard? changeGuard = null, Func<string, IEnumerable<string>>? blobReferences = null)
    {
        SyncIds.EnsureCollection(name);
        if (schemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        Name = name;
        SchemaVersion = schemaVersion;
        Policy = policy;
        CreateConflictCopy = createConflictCopy;
        ChangeGuard = changeGuard;
        BlobReferences = blobReferences;
    }

    public string Name { get; }

    /// <summary>The highest schema this build understands; newer records are kept untouched and never overwritten.</summary>
    public int SchemaVersion { get; }

    public SyncConflictPolicy Policy { get; }

    /// <summary>KeepBoth only: turns the losing local body JSON into the body of its copy (e.g. rename the title).</summary>
    public Func<string, string>? CreateConflictCopy { get; }

    /// <summary>When set, a pull that would delete many of this collection's records is held for the user.</summary>
    public SyncChangeGuard? ChangeGuard { get; }

    /// <summary>
    /// The blob ids a record body uses. Records wait until their blobs are on the server (docs/vault-design.md, I6),
    /// and blobs no record uses any more are cleaned up. Collections that store files must set it.
    /// </summary>
    public Func<string, IEnumerable<string>>? BlobReferences { get; }
}

/// <summary>
/// Protects a collection whose records must never vanish in bulk (the vault). A pull that deletes at least
/// <see cref="Threshold"/> live records of the collection — which a bug, a mistake on another device or a stolen
/// device could cause — is not applied: the engine stops in <see cref="SyncState.Held"/> and the user either applies
/// the deletions (<see cref="SyncEngine.ApproveHeld"/>) or keeps this device's records and uploads them again
/// (<see cref="SyncEngine.RejectHeld"/>).
/// </summary>
/// <param name="IsExpendable">
/// Deleting a record that is expendable is expected (e.g. emptying the trash) and not counted. Gets the local body JSON.
/// </param>
public sealed record SyncChangeGuard(Func<string, bool>? IsExpendable = null)
{
    /// <summary>
    /// 20 % of the live records, but at least 3 and never more than 10: a small collection losing most of its records
    /// and a large one losing ten are both suspicious.
    /// </summary>
    public static int Threshold(int liveRecords) => Math.Clamp((int)Math.Ceiling(liveRecords * 0.2), 3, 10);
}
