using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Core.Sync;

public enum SyncState
{
    /// <summary>No server or no key on this device: data stays local.</summary>
    NotConfigured,
    Idle,
    Syncing,
    /// <summary>The last attempt could not reach the server; local edits are kept and retried.</summary>
    Offline,
    /// <summary>The server rejected this device's token (revoked, expired, or missing a scope).</summary>
    Unauthorized,
    /// <summary>The account's storage quota is full; local edits are kept but not uploaded.</summary>
    QuotaExceeded,
    /// <summary>The account key was rotated on another device: unlock again with the passphrase.</summary>
    KeyChanged,
    Error,
}

public sealed record SyncStatus(SyncState State, DateTimeOffset? LastSyncedAt, string? LastError);

public enum SyncRunOutcome
{
    Completed,
    NotConfigured,
    Offline,
    Unauthorized,
    QuotaExceeded,
    KeyChanged,
    Failed,
}

public sealed record SyncRunResult(SyncRunOutcome Outcome, int Pushed, int Pulled, int Conflicts);

public sealed class SyncRecordsChangedEventArgs(string collection, IReadOnlyList<string> ids) : EventArgs
{
    public string Collection { get; } = collection;
    public IReadOnlyList<string> Ids { get; } = ids;
}

/// <summary>What the shell and the General → Sync page use.</summary>
public interface ISyncService
{
    SyncStatus Status { get; }

    /// <summary>Raised on a thread-pool thread; marshal to the UI with <c>IUiDispatcher</c>.</summary>
    event EventHandler<SyncStatus>? StatusChanged;

    /// <summary>Push local edits, then pull everything new. Never throws for network or server errors.</summary>
    Task<SyncRunResult> SyncNowAsync(CancellationToken ct = default);

    /// <summary>Schedules a debounced <see cref="SyncNowAsync"/> (a no-op while sync is not configured).</summary>
    void RequestSync();
}

/// <summary>
/// Moves records between the local replica and the server: push (compare-and-set on the record version), resolve
/// rejected pushes with the collection's policy, then pull by global seq. Runs are serialized. See
/// docs/sync-protocol.md for the full protocol.
/// </summary>
public sealed class SyncEngine : ISyncService, IDisposable
{
    internal const string MainCursor = "*";
    private const int PushBatchSize = 100;
    private const int PullPageSize = 500;
    // Push keeps going while batches make progress (a first upload can be thousands of records); this only stops
    // a pathological loop, e.g. a record that is edited during every single push.
    private const int MaxPushRounds = 1000;

    private readonly SyncDatabase _db;
    private readonly ISyncTransport _transport;
    private readonly IMasterKeyStore _keys;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<string, SyncCollectionDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly Timer _timer;
    private Timer? _periodic;
    private readonly object _keyGate = new();
    private SyncKeyring? _keyring;
    private volatile bool _disposed;

    public SyncEngine(
        SyncDatabase db,
        ISyncTransport transport,
        IMasterKeyStore keys,
        IEnumerable<SyncCollectionDescriptor> descriptors,
        ILogger<SyncEngine>? logger = null,
        TimeProvider? time = null,
        TimeSpan? debounce = null)
    {
        _db = db;
        _transport = transport;
        _keys = keys;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        foreach (var descriptor in descriptors) Register(descriptor);
        _timer = new Timer(_ => _ = RunScheduledAsync(), null, Timeout.Infinite, Timeout.Infinite);
        Status = new SyncStatus(transport.IsConfigured ? SyncState.Idle : SyncState.NotConfigured, null, null);
    }

    public SyncStatus Status { get; private set; }

    public event EventHandler<SyncStatus>? StatusChanged;

    /// <summary>
    /// Raised when the server holds data sealed with a newer key epoch (the account key was rotated elsewhere). The
    /// run stops without skipping anything; <see cref="SyncSetupService"/> then asks for the passphrase again.
    /// </summary>
    public event EventHandler? KeyEpochChanged;

    /// <summary>Raised after a run for every collection whose records changed (remote edits, conflict copies).</summary>
    internal event EventHandler<SyncRecordsChangedEventArgs>? RecordsChanged;

    internal SyncDatabase Database => _db;

    internal TimeProvider Time => _time;

    internal void Register(SyncCollectionDescriptor descriptor) => _descriptors[descriptor.Name] = descriptor;

    /// <summary>Syncs now and then every <paramref name="interval"/> (default 5 minutes) while Helm runs.</summary>
    public void Start(TimeSpan? interval = null)
    {
        if (_disposed || _periodic is not null) return;
        var every = interval ?? TimeSpan.FromMinutes(5);
        _periodic = new Timer(_ => { if (_transport.IsConfigured) _ = RunScheduledAsync(); }, null, TimeSpan.FromSeconds(3), every);
    }

    public void RequestSync()
    {
        if (_disposed || !_transport.IsConfigured) return;
        try { _timer.Change(_debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Forgets the cached key, e.g. after sync was set up or reset on this device.</summary>
    public void ReloadKey()
    {
        lock (_keyGate)
        {
            _keyring?.Dispose();
            _keyring = null;
        }
    }

    public async Task<SyncRunResult> SyncNowAsync(CancellationToken ct = default)
    {
        if (!_transport.IsConfigured) return NotConfigured();
        var keyring = GetKeyring();
        if (keyring is null) return NotConfigured();

        await _run.WaitAsync(ct).ConfigureAwait(false);
        var run = new RunState();
        try
        {
            SetStatus(Status with { State = SyncState.Syncing });
            await PushAsync(keyring, run, ct).ConfigureAwait(false);
            await PullAsync(keyring, run, ct).ConfigureAwait(false);
            // Conflicts found while pulling may have kept local edits (rebased or copied): send them now.
            if (run.PendingPush) await PushAsync(keyring, run, ct).ConfigureAwait(false);
            SetStatus(new SyncStatus(SyncState.Idle, _time.GetUtcNow(), null));
            return run.Result(SyncRunOutcome.Completed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(Status with { State = SyncState.Idle });
            throw;
        }
        catch (SyncKeyEpochException ex)
        {
            _logger.LogInformation("Sync data uses key epoch {Epoch}; this device must unlock again", ex.Epoch);
            SetStatus(Status with { State = SyncState.KeyChanged, LastError = ex.Message });
            try { KeyEpochChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception handlerEx) { _logger.LogError(handlerEx, "A key-change handler failed"); }
            return run.Result(SyncRunOutcome.KeyChanged);
        }
        catch (SyncQuotaException ex)
        {
            _logger.LogWarning("Sync storage quota exceeded: {Message}", ex.Message);
            SetStatus(Status with { State = SyncState.QuotaExceeded, LastError = ex.Message });
            return run.Result(SyncRunOutcome.QuotaExceeded);
        }
        catch (SyncAuthException ex)
        {
            _logger.LogWarning("Sync token rejected: {Code}", ex.Code);
            SetStatus(Status with { State = SyncState.Unauthorized, LastError = ex.Message });
            return run.Result(SyncRunOutcome.Unauthorized);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or TaskCanceledException)
        {
            _logger.LogInformation(ex, "Sync server unreachable; local edits are kept for the next attempt");
            SetStatus(Status with { State = SyncState.Offline, LastError = ex.Message });
            return run.Result(SyncRunOutcome.Offline);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync run failed");
            SetStatus(Status with { State = SyncState.Error, LastError = ex.Message });
            return run.Result(SyncRunOutcome.Failed);
        }
        finally
        {
            _run.Release();
            RaiseChanges(run);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _periodic?.Dispose();
        ReloadKey();
    }

    private async Task PushAsync(SyncKeyring keyring, RunState run, CancellationToken ct)
    {
        for (var round = 0; round < MaxPushRounds; round++)
        {
            var dirty = _db.GetDirty(PushBatchSize);
            if (dirty.Count == 0) return;

            var items = dirty.Select(row => new PushItem(row.Collection, row.Id, row.Version, row.Deleted,
                keyring.Seal(row.Collection, row.Id, new SealedContent(row.SchemaVersion, row.UpdatedAtMs, row.DeviceId,
                    row.Deleted ? null : row.Body)))).ToList();
            var outcomes = await _transport.PushAsync(items, ct).ConfigureAwait(false);

            var progressed = false;
            SyncKeyEpochException? newerEpoch = null;
            _db.InTransaction(() =>
            {
                var byKey = dirty.ToDictionary(row => (row.Collection, row.Id));
                foreach (var outcome in outcomes)
                {
                    if (!byKey.TryGetValue((outcome.Collection, outcome.Id), out var row)) continue;
                    if (outcome.Accepted)
                    {
                        _db.MarkPushed(row.Collection, row.Id, row.LocalRev, outcome.Version);
                        run.Pushed++;
                        progressed = true;
                    }
                    else
                    {
                        run.Conflicts++;
                        try
                        {
                            progressed |= ResolveRejectedPush(keyring, row, outcome.Current, run);
                        }
                        catch (SyncKeyEpochException ex)
                        {
                            // Keep the accepted items of this batch; stop after the transaction.
                            newerEpoch = ex;
                        }
                    }
                }
            });
            if (newerEpoch is not null) throw newerEpoch;
            // Nothing could be resolved (e.g. unreadable server copies): stop instead of pushing the same batch again.
            if (!progressed) return;
        }
    }

    private bool ResolveRejectedPush(SyncKeyring keyring, SyncRow local, RemoteRecord? current, RunState run)
    {
        if (current is null)
        {
            // The server has no such record (e.g. purged): recreate it from the local copy.
            _db.Rebase(local.Collection, local.Id, 0);
            return true;
        }
        if (!TryOpen(keyring, current, out var remote)) return false;
        Resolve(local, current, remote, run);
        return true;
    }

    private async Task PullAsync(SyncKeyring keyring, RunState run, CancellationToken ct)
    {
        var since = _db.GetCursor(MainCursor);
        while (true)
        {
            var page = await _transport.PullAsync(since, PullPageSize, ct).ConfigureAwait(false);
            var next = Math.Max(since, page.NextSeq);
            _db.InTransaction(() =>
            {
                foreach (var record in page.Records) ApplyPulled(keyring, record, run);
                _db.SetCursor(MainCursor, next);
            });
            if (!page.HasMore || next == since) return;
            since = next;
        }
    }

    private void ApplyPulled(SyncKeyring keyring, RemoteRecord record, RunState run)
    {
        if (!SyncIds.IsValidCollection(record.Collection) || !SyncIds.IsValidId(record.Id))
        {
            _logger.LogWarning("Ignoring a pulled record with an invalid collection or id");
            return;
        }

        var local = _db.Get(record.Collection, record.Id);
        // Already known, typically this device's own push coming back.
        if (local is not null && local.Version >= record.Version) return;
        if (!TryOpen(keyring, record, out var remote)) return;

        run.Pulled++;
        if (local is { Dirty: true })
        {
            run.Conflicts++;
            Resolve(local, record, remote, run);
            return;
        }
        _db.ApplyRemote(record.Collection, record.Id, record.Version, remote, record.Deleted);
        run.Changed(record.Collection, record.Id);
    }

    /// <summary>A local edit and a server edit were both made on the same base version.</summary>
    private void Resolve(SyncRow local, RemoteRecord record, SealedContent remote, RunState run)
    {
        _descriptors.TryGetValue(local.Collection, out var descriptor);
        var policy = descriptor?.Policy ?? SyncConflictPolicy.LastWriterWins;
        // Never overwrite a record written by a newer schema: this build would drop the fields it does not know.
        if (descriptor is not null && remote.SchemaVersion > descriptor.SchemaVersion) policy = SyncConflictPolicy.RemoteWins;

        // Re-sealing after a key rotation is not an edit: whatever the server has now wins.
        if (local.Reseal) policy = SyncConflictPolicy.RemoteWins;

        switch (policy)
        {
            case SyncConflictPolicy.LastWriterWins when IsLocalNewer(local, remote):
                _db.Rebase(local.Collection, local.Id, record.Version);
                run.PendingPush = true;
                break;

            case SyncConflictPolicy.KeepBoth when !local.Deleted && record.Deleted:
                // An edit beats a deletion: keep the edit and resurrect the record.
                _db.Rebase(local.Collection, local.Id, record.Version);
                run.PendingPush = true;
                break;

            case SyncConflictPolicy.KeepBoth when !local.Deleted && local.Body != remote.Body:
                var copyId = SyncIds.NewId(_time);
                _db.WriteLocal(local.Collection, copyId, local.SchemaVersion, ConflictCopy(descriptor, local.Body), deleted: false,
                    local.UpdatedAtMs);
                _db.ApplyRemote(local.Collection, local.Id, record.Version, remote, record.Deleted);
                run.Changed(local.Collection, copyId);
                run.PendingPush = true;
                _logger.LogInformation("Sync conflict in {Collection}: kept the local edit as {CopyId}", local.Collection, copyId);
                break;

            default:
                _db.ApplyRemote(local.Collection, local.Id, record.Version, remote, record.Deleted);
                break;
        }
        run.Changed(local.Collection, local.Id);
    }

    private string? ConflictCopy(SyncCollectionDescriptor? descriptor, string? body)
    {
        if (body is null || descriptor?.CreateConflictCopy is not { } copy) return body;
        try
        {
            return copy(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conflict-copy hook failed for {Collection}; keeping the body unchanged", descriptor.Name);
            return body;
        }
    }

    private static bool IsLocalNewer(SyncRow local, SealedContent remote) =>
        local.UpdatedAtMs != remote.UpdatedAtMs
            ? local.UpdatedAtMs > remote.UpdatedAtMs
            : string.CompareOrdinal(local.DeviceId, remote.DeviceId) > 0;

    private bool TryOpen(SyncKeyring keyring, RemoteRecord record, out SealedContent content)
    {
        try
        {
            content = keyring.Open(record.Collection, record.Id, record.Payload);
            return true;
        }
        catch (CryptographicException ex)
        {
            // Wrong key or a tampered payload. Skip it rather than failing the whole run.
            _logger.LogWarning(ex, "Could not decrypt {Collection}/{Id}; skipping it", record.Collection, record.Id);
            content = null!;
            return false;
        }
    }

    private SyncKeyring? GetKeyring()
    {
        lock (_keyGate)
        {
            if (_keyring is not null) return _keyring;
            var data = _keys.Load();
            if (data is null) return null;
            using var set = SyncKeySet.TryParse(data);
            CryptographicOperations.ZeroMemory(data);
            if (set is null)
            {
                _logger.LogWarning("The stored sync key is unreadable; unlock sync again");
                return null;
            }
            _keyring = new SyncKeyring(set);
            return _keyring;
        }
    }

    private SyncRunResult NotConfigured()
    {
        if (Status.State != SyncState.NotConfigured) SetStatus(new SyncStatus(SyncState.NotConfigured, Status.LastSyncedAt, null));
        return new SyncRunResult(SyncRunOutcome.NotConfigured, 0, 0, 0);
    }

    private void SetStatus(SyncStatus status)
    {
        Status = status;
        try { StatusChanged?.Invoke(this, status); }
        catch (Exception ex) { _logger.LogError(ex, "A sync status handler failed"); }
    }

    private void RaiseChanges(RunState run)
    {
        foreach (var (collection, ids) in run.ChangedIds)
        {
            try { RecordsChanged?.Invoke(this, new SyncRecordsChangedEventArgs(collection, ids.ToList())); }
            catch (Exception ex) { _logger.LogError(ex, "A sync change handler failed for {Collection}", collection); }
        }
    }

    /// <summary>Timer callbacks run on the thread pool, where an unhandled exception terminates Helm.</summary>
    private async Task RunScheduledAsync()
    {
        if (_disposed) return;
        try
        {
            await SyncNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled sync failed");
        }
    }

    private sealed class RunState
    {
        public int Pushed;
        public int Pulled;
        public int Conflicts;
        public bool PendingPush;
        public Dictionary<string, HashSet<string>> ChangedIds { get; } = new(StringComparer.Ordinal);

        public void Changed(string collection, string id)
        {
            if (!ChangedIds.TryGetValue(collection, out var ids)) ChangedIds[collection] = ids = new HashSet<string>(StringComparer.Ordinal);
            ids.Add(id);
        }

        public SyncRunResult Result(SyncRunOutcome outcome) => new(outcome, Pushed, Pulled, Conflicts);
    }
}
