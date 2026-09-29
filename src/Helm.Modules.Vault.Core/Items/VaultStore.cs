using Helm.Core.Sync;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Modules.Vault.Items;

/// <summary>Another record of the same item, made when two devices edited it before syncing (KeepBoth).</summary>
public sealed record VaultConflict(string RecordId, VaultItem Item, DateTimeOffset UpdatedAt);

/// <summary>A decrypted item as the UI shows it.</summary>
public sealed record VaultEntry(
    string Uid,
    VaultItem Item,
    bool Trashed,
    DateTimeOffset? TrashedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<VaultConflict> Conflicts,
    string RecordId)
{
    public override string ToString() => $"VaultEntry({Uid})";
}

/// <summary>A record that does not open with this vault's key: shown as a problem, never dropped or overwritten.</summary>
public sealed record VaultUnreadable(string RecordId, string Uid, string Reason);

/// <summary>
/// The vault's items on top of the synced collection <c>vault.items</c>. It enforces the loss-prevention invariants of
/// docs/vault-design.md: nothing is removed except from the trash (I1), every edit keeps the previous version (I2),
/// and concurrent edits stay visible as conflicts until the user picks one (I3). Decrypted items are cached only
/// while the vault is unlocked. Thread-safe; <see cref="Changed"/> is raised on any thread.
/// </summary>
public sealed class VaultStore : IDisposable
{
    public const string Collection = "vault.items";

    private readonly ISyncedCollection<VaultItemRecord> _records;
    private readonly VaultSession _session;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    // Keyed by record id; the nonce identifies one sealed version of the record.
    private readonly Dictionary<string, (string Nonce, VaultItem Item)> _cache = new(StringComparer.Ordinal);

    public VaultStore(ISyncedCollection<VaultItemRecord> records, VaultSession session, TimeProvider? time = null, ILogger<VaultStore>? logger = null)
    {
        _records = records;
        _session = session;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _records.Changed += OnRecordsChanged;
        _session.Locking += OnLocking;
    }

    public event EventHandler? Changed;

    /// <summary>The collection options: KeepBoth, and bulk deletions of items that are not in the trash are held.</summary>
    public static SyncedCollectionOptions<VaultItemRecord> Options { get; } = new()
    {
        Name = Collection,
        ConflictPolicy = SyncConflictPolicy.KeepBoth,
        GuardDeletions = true,
        IsExpendable = record => record.Trashed,
        BlobReferences = record => record.Blobs,
    };

    /// <summary>Records of this device's replica, including ones the vault cannot open (readable while locked).</summary>
    public int RecordCount => _records.All().Count;

    /// <summary>Live items, by title.</summary>
    public IReadOnlyList<VaultEntry> Items() => Entries().Where(e => !e.Trashed).ToList();

    /// <summary>Trashed items, most recently trashed first.</summary>
    public IReadOnlyList<VaultEntry> Trash() => Entries().Where(e => e.Trashed).OrderByDescending(e => e.TrashedAt).ToList();

    public VaultEntry? Get(string uid) => Entries().FirstOrDefault(e => e.Uid == uid);

    /// <summary>Records of this vault that cannot be opened, and records of other vaults (to merge).</summary>
    public IReadOnlyList<VaultUnreadable> Unreadable()
    {
        var key = _session.Key;
        var vaultId = _session.VaultId;
        var problems = new List<VaultUnreadable>();
        foreach (var record in _records.All())
        {
            if (record.Value.Vault != vaultId)
            {
                problems.Add(new VaultUnreadable(record.Id, record.Value.Uid, "Belongs to another vault"));
                continue;
            }
            if (TryOpen(key, record.Id, record.Value, out _, out var reason)) continue;
            problems.Add(new VaultUnreadable(record.Id, record.Value.Uid, reason!));
        }
        return problems;
    }

    /// <returns>The new item's uid.</returns>
    public string Add(VaultItem item)
    {
        var key = _session.Key;
        var uid = SyncIds.NewId(_time);
        var now = Now();
        var sealedItem = item with { CreatedAtMs = now, ModifiedAtMs = now, History = [] };
        _records.Upsert(uid, VaultItemSealer.Seal(key, RequireVaultId(), 1, uid, sealedItem, trashed: false, trashedAtMs: null));
        _session.Touch();
        return uid;
    }

    /// <summary>Saves an edit; the version it replaces goes to the top of the history (I2).</summary>
    public void Save(string uid, VaultItem edited)
    {
        var key = _session.Key;
        var (recordId, current, record) = Primary(key, uid);
        if (edited.SameContentAs(current)) return;
        var saved = WithHistory(edited with { CreatedAtMs = current.CreatedAtMs, ModifiedAtMs = Now() }, [current], current.History);
        _records.Upsert(recordId, VaultItemSealer.Seal(key, record.Vault, record.Epoch, uid, saved, record.Trashed, record.TrashedAtMs));
        _session.Touch();
    }

    /// <summary>
    /// Stars or unstars an item. Not a new version: the history keeps the last real edits, not star clicks.
    /// </summary>
    public void SetFavorite(string uid, bool favorite)
    {
        var key = _session.Key;
        var (recordId, current, record) = Primary(key, uid);
        if (current.Favorite == favorite) return;
        var saved = current with { Favorite = favorite, ModifiedAtMs = Now() };
        _records.Upsert(recordId, VaultItemSealer.Seal(key, record.Vault, record.Epoch, uid, saved, record.Trashed, record.TrashedAtMs));
        _session.Touch();
    }

    /// <summary>Brings back an earlier version; the current one goes into the history, so this can be undone too.</summary>
    public void RestoreVersion(string uid, VaultItemVersion version) =>
        Save(uid, version.Item with { History = [] });

    /// <summary>Moves the item (and any conflict copies) to the trash, where it stays <see cref="VaultSettings.TrashDays"/> days.</summary>
    public void MoveToTrash(string uid) => SetTrashed(uid, trashed: true);

    public void RestoreFromTrash(string uid) => SetTrashed(uid, trashed: false);

    /// <summary>Deletes a trashed item for good, on every device. Refuses items that are not in the trash (I1).</summary>
    public void Purge(string uid)
    {
        var records = RecordsOf(uid);
        if (records.Count == 0) return;
        if (records.Any(r => !r.Value.Trashed)) throw new InvalidOperationException("Only items in the trash can be deleted for good.");
        foreach (var record in records) _records.Delete(record.Id);
        _logger.LogInformation("Vault item purged from the trash");
    }

    /// <summary>Deletes items that have been in the trash longer than <paramref name="keep"/>. Works while locked.</summary>
    /// <returns>How many records were deleted.</returns>
    public int PurgeExpired(TimeSpan keep)
    {
        var cutoff = Now() - (long)keep.TotalMilliseconds;
        var expired = _records.All().Where(r => r.Value.Trashed && r.Value.TrashedAtMs is { } at && at < cutoff).ToList();
        foreach (var record in expired) _records.Delete(record.Id);
        if (expired.Count > 0) _logger.LogInformation("Deleted {Count} vault records that were in the trash for over {Days} days", expired.Count, keep.TotalDays);
        return expired.Count;
    }

    /// <summary>
    /// Ends a conflict: <paramref name="keepRecordId"/> becomes the item, and every other version is kept at the top
    /// of its history, so choosing is never a loss.
    /// </summary>
    public void ResolveConflict(string uid, string keepRecordId)
    {
        var key = _session.Key;
        var (primaryId, _, primaryRecord) = Primary(key, uid);
        var versions = RecordsOf(uid)
            .Select(r => (r.Id, r.UpdatedAt, Item: TryOpen(key, r.Id, r.Value, out var item, out _) ? item : null))
            .Where(v => v.Item is not null)
            .ToList();
        var kept = versions.FirstOrDefault(v => v.Id == keepRecordId).Item ?? throw new ArgumentException("No such version of this item.", nameof(keepRecordId));
        var others = versions.Where(v => v.Id != keepRecordId).OrderByDescending(v => v.UpdatedAt).Select(v => v.Item!).ToList();

        var merged = WithHistory(kept with { ModifiedAtMs = Now() },
            [.. others, .. others.SelectMany(o => o.History.Select(h => h.Item))], kept.History);
        _records.Upsert(primaryId, VaultItemSealer.Seal(key, primaryRecord.Vault, primaryRecord.Epoch, uid, merged, primaryRecord.Trashed, primaryRecord.TrashedAtMs));
        foreach (var version in versions.Where(v => v.Id != primaryId)) _records.Delete(version.Id);
        _logger.LogInformation("Resolved a vault conflict ({Count} versions)", versions.Count);
    }

    /// <summary>This vault's records exactly as synced (still sealed): what a backup snapshot stores.</summary>
    internal IReadOnlyList<SyncedItem<VaultItemRecord>> SealedRecords() =>
        _records.All().Where(r => r.Value.Vault == _session.VaultId).ToList();

    /// <summary>Writes a sealed record back as it was (restore from a backup).</summary>
    internal void RestoreSealed(string recordId, VaultItemRecord record) => _records.Upsert(recordId, record);

    /// <summary>Every file any readable item, conflict copy, trashed item or history entry uses.</summary>
    internal IReadOnlyList<BlobRef> ReferencedBlobs()
    {
        var key = _session.Key;
        var blobs = new Dictionary<string, BlobRef>(StringComparer.Ordinal);
        foreach (var record in SealedRecords())
        {
            if (!TryOpen(key, record.Id, record.Value, out var item, out _)) continue;
            foreach (var blob in item!.AllBlobs()) blobs.TryAdd(blob.Id, blob);
        }
        return blobs.Values.ToList();
    }

    /// <summary>Opens a sealed record with a key (e.g. from a backup of another vault), or null.</summary>
    public static VaultItem? TryOpenWith(VaultKey key, VaultItemRecord record)
    {
        try
        {
            return VaultItemSealer.Open(key, record);
        }
        catch (VaultKeyException)
        {
            return null;
        }
    }

    /// <summary>Adds an item sealed with this vault's key, keeping its uid and trash state (restore).</summary>
    internal void AddRestored(string uid, VaultItem item, bool trashed, long? trashedAtMs) =>
        _records.Upsert(uid, VaultItemSealer.Seal(_session.Key, RequireVaultId(), 1, uid, item, trashed, trashedAtMs));

    public void Dispose()
    {
        _records.Changed -= OnRecordsChanged;
        _session.Locking -= OnLocking;
        OnLocking(this, EventArgs.Empty);
    }

    /// <summary>
    /// <paramref name="item"/> with the given older versions (newest first) on top of <paramref name="history"/>, keeping
    /// the newest <see cref="VaultItem.MaxHistory"/> distinct versions.
    /// </summary>
    private static VaultItem WithHistory(VaultItem item, IEnumerable<VaultItem> olderVersions, IReadOnlyList<VaultItemVersion> history)
    {
        var entries = olderVersions.Select(v => new VaultItemVersion(v.ModifiedAtMs, v with { History = [] }))
            .Concat(history)
            .Where(v => !v.Item.SameContentAs(item))
            .DistinctBy(v => (v.SavedAtMs, v.Item.Title, v.Item.Fields.Count, v.Item.Notes))
            .OrderByDescending(v => v.SavedAtMs)
            .Take(VaultItem.MaxHistory)
            .ToList();
        return item with { History = entries };
    }

    private void SetTrashed(string uid, bool trashed)
    {
        var key = _session.Key;
        var records = RecordsOf(uid);
        if (records.Count == 0) throw new ArgumentException("No such item.", nameof(uid));
        var at = trashed ? Now() : (long?)null;
        foreach (var record in records)
        {
            if (record.Value.Trashed == trashed) continue;
            if (!TryOpen(key, record.Id, record.Value, out var item, out var reason))
                throw new VaultKeyException($"This item cannot be changed: {reason}");
            _records.Upsert(record.Id, VaultItemSealer.Seal(key, record.Value.Vault, record.Value.Epoch, uid, item!, trashed, at));
        }
        _session.Touch();
    }

    private List<VaultEntry> Entries()
    {
        var key = _session.Key;
        var vaultId = _session.VaultId;
        var entries = new List<VaultEntry>();
        foreach (var group in _records.All().Where(r => r.Value.Vault == vaultId).GroupBy(r => r.Value.Uid))
        {
            var opened = group
                .Select(r => (Record: r, Item: TryOpen(key, r.Id, r.Value, out var item, out _) ? item : null))
                .Where(v => v.Item is not null)
                .ToList();
            if (opened.Count == 0) continue;
            var primary = opened.FirstOrDefault(v => v.Record.Id == group.Key);
            if (primary.Item is null) primary = opened.OrderBy(v => v.Record.UpdatedAt).First();
            var conflicts = opened.Where(v => v.Record.Id != primary.Record.Id)
                .Select(v => new VaultConflict(v.Record.Id, v.Item!, v.Record.UpdatedAt)).ToList();
            var value = primary.Record.Value;
            entries.Add(new VaultEntry(group.Key, primary.Item!, value.Trashed,
                value.TrashedAtMs is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null, primary.Record.UpdatedAt, conflicts, primary.Record.Id));
        }
        return entries.OrderBy(e => e.Item.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Uid, StringComparer.Ordinal).ToList();
    }

    /// <summary>The record that carries the item's id (or the oldest copy when that one is gone), opened.</summary>
    private (string RecordId, VaultItem Item, VaultItemRecord Record) Primary(VaultKey key, string uid)
    {
        var records = RecordsOf(uid);
        var primary = records.FirstOrDefault(r => r.Id == uid) ?? records.OrderBy(r => r.UpdatedAt).FirstOrDefault()
            ?? throw new ArgumentException("No such item.", nameof(uid));
        if (!TryOpen(key, primary.Id, primary.Value, out var item, out var reason))
            throw new VaultKeyException($"This item cannot be changed: {reason}");
        return (primary.Id, item!, primary.Value);
    }

    private List<SyncedItem<VaultItemRecord>> RecordsOf(string uid) =>
        _records.All().Where(r => r.Value.Uid == uid && r.Value.Vault == _session.VaultId).ToList();

    private bool TryOpen(VaultKey key, string recordId, VaultItemRecord record, out VaultItem? item, out string? reason)
    {
        var nonce = record.Data.Length >= 12 ? Convert.ToHexString(record.Data, 0, 12) : "";
        lock (_gate)
        {
            if (_cache.TryGetValue(recordId, out var cached) && cached.Nonce == nonce)
            {
                item = cached.Item;
                reason = null;
                return true;
            }
        }
        try
        {
            item = VaultItemSealer.Open(key, record);
            lock (_gate) _cache[recordId] = (nonce, item);
            reason = null;
            return true;
        }
        catch (VaultKeyException ex)
        {
            _logger.LogWarning("Vault record cannot be opened: {Reason}", ex.Message);
            item = null;
            reason = ex.Message;
            return false;
        }
    }

    private string RequireVaultId() => _session.VaultId ?? throw new InvalidOperationException("Create the vault first.");

    private void OnLocking(object? sender, EventArgs e)
    {
        lock (_gate) _cache.Clear();
    }

    private void OnRecordsChanged(object? sender, SyncedChangedEventArgs e)
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogError(ex, "A vault change handler failed"); }
    }

    private long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();
}
