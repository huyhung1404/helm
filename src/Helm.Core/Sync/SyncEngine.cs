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
    /// <summary>
    /// A pull would delete many records of a guarded collection (see <see cref="SyncChangeGuard"/>). Nothing more is
    /// pulled until the user decides (<see cref="SyncEngine.Hold"/>); local edits are still pushed.
    /// </summary>
    Held,
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
    Held,
    Failed,
}

/// <summary>A pull that was not applied because it deletes <see cref="Records"/> of a guarded collection.</summary>
/// <param name="Records">The records the server has deleted, with the server version of each deletion.</param>
public sealed record SyncHold(string Collection, int LiveRecords, IReadOnlyList<(string Id, long Version)> Records);

internal sealed class SyncHeldException(SyncHold hold) : Exception($"Sync would delete {hold.Records.Count} {hold.Collection} records.")
{
    public SyncHold Hold { get; } = hold;
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
    // Pages (of PullPageSize) held in memory at most while judging guarded deletions together.
    private const int MaxHeldPages = 20;
    private const string BlobGcMeta = "blob_gc_at";
    private static readonly TimeSpan BlobGcInterval = TimeSpan.FromDays(1);
    // Selective sync (device-local): prefixes this device does not sync, and re-enabled ones still to download again.
    private const string ExcludeMeta = "sync_exclude";
    private const string CatchUpMeta = "sync_catchup";
    // Set when the server cleaned up deletions this device may have missed: the next run pulls everything from 0.
    private const string ResyncMeta = "sync_resync";
    private const string TombstoneGcMeta = "tombstone_gc_at";
    /// <summary>Local deletions the server has are dropped after this long (the server keeps them 90 days by default).</summary>
    internal static readonly TimeSpan LocalTombstoneRetention = TimeSpan.FromDays(90);
    // A resync that keeps meeting fresh clean-ups on the server gives up after this many restarts (and retries next run).
    private const int MaxResyncAttempts = 3;

    private readonly SyncDatabase _db;
    private readonly ISyncTransport _transport;
    private readonly IMasterKeyStore _keys;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private TimeSpan _pollInterval = BackgroundPollInterval;
    private readonly ConcurrentDictionary<string, SyncCollectionDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly Timer _timer;
    private Timer? _periodic;
    private readonly object _keyGate = new();
    private SyncKeyring? _keyring;
    private volatile bool _disposed;
    private volatile bool _applyHold;
    private readonly IBlobSync? _blobs;
    private readonly object _selectionGate = new();
    private string[] _excluded = [];
    private readonly object _liveGate = new();
    private SyncLiveChannel? _live;
    // Seqs of this device's own accepted pushes (their live announcements are not news), and the highest seq
    // another device announced.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _ownSeqs = new();
    private long _liveSeq;
    // Bumped on every change of the selection: a resync that saw it change must not drop anything.
    private int _selectionVersion;
    private long _lastRunTicks;

    public SyncEngine(
        SyncDatabase db,
        ISyncTransport transport,
        IMasterKeyStore keys,
        IEnumerable<SyncCollectionDescriptor> descriptors,
        ILogger<SyncEngine>? logger = null,
        TimeProvider? time = null,
        TimeSpan? debounce = null,
        IBlobSync? blobs = null)
    {
        _blobs = blobs;
        _db = db;
        _transport = transport;
        _keys = keys;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        foreach (var descriptor in descriptors) Register(descriptor);
        _excluded = ParseList(db.GetMetaValue(ExcludeMeta));
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

    /// <summary>The deletions waiting for the user while <see cref="Status"/> is <see cref="SyncState.Held"/>; else null.</summary>
    public SyncHold? Hold { get; private set; }

    internal SyncDatabase Database => _db;

    internal TimeProvider Time => _time;

    internal void Register(SyncCollectionDescriptor descriptor) => _descriptors[descriptor.Name] = descriptor;

    /// <summary>
    /// Collection prefixes (e.g. "vault.") this device does not sync: nothing of them is uploaded or downloaded. Local
    /// data stays as it is, and edits made meanwhile are uploaded when the prefix is synced again.
    /// </summary>
    public IReadOnlyList<string> ExcludedPrefixes => Volatile.Read(ref _excluded);

    public bool IsExcluded(string collection) => ExcludedPrefixes.Any(p => SyncPullFilter.Covers(p, collection));

    /// <summary>
    /// Stops or resumes syncing every collection under <paramref name="prefix"/> ("tool." with the dot) on this device.
    /// Resuming downloads that part again in full, and drops what was deleted elsewhere meanwhile.
    /// </summary>
    public void SetExcluded(string prefix, bool excluded)
    {
        if (!prefix.EndsWith('.') || !SyncIds.IsValidCollection(prefix)) throw new ArgumentException("A prefix is a collection name ending in \".\".", nameof(prefix));
        lock (_selectionGate)
        {
            var current = ExcludedPrefixes.ToList();
            if (current.Contains(prefix) == excluded) return;
            var catchUp = ParseList(_db.GetMetaValue(CatchUpMeta)).ToList();
            if (excluded)
            {
                current.Add(prefix);
                catchUp.Remove(prefix);
            }
            else
            {
                current.Remove(prefix);
                if (!catchUp.Contains(prefix)) catchUp.Add(prefix);
            }
            var list = current.Order(StringComparer.Ordinal).ToArray();
            _db.InTransaction(() =>
            {
                _db.SetMetaValue(ExcludeMeta, list.Length == 0 ? null : string.Join(',', list));
                _db.SetMetaValue(CatchUpMeta, catchUp.Count == 0 ? null : string.Join(',', catchUp));
            });
            Volatile.Write(ref _excluded, list);
            Interlocked.Increment(ref _selectionVersion);
        }
        _logger.LogInformation("Sync of {Prefix}* on this device: {State}", prefix, excluded ? "off" : "on");
        RequestSync();
    }

    /// <summary>True while the live channel is connected: other devices' changes arrive within seconds.</summary>
    public bool IsLive => _live?.Connected == true;

    /// <summary>
    /// Keeps the server's live channel open (on) or closes it (off), e.g. only while the app is in front on a phone.
    /// While it is connected, the periodic poll runs at most every <see cref="BackgroundPollInterval"/>. A no-op for a
    /// transport without a live channel.
    /// </summary>
    public void SetLive(bool enabled)
    {
        if (_disposed || _transport is not ISyncLiveTransport transport) return;
        lock (_liveGate)
        {
            if (!enabled)
            {
                _live?.Stop();
                return;
            }
            // Connecting or dropping changes what the status says ("live"), not the status itself.
            _live ??= new SyncLiveChannel(transport, () => _transport.IsConfigured && GetKeyring() is not null, OnLiveSeq,
                _ => RaiseStatusChanged(), _logger);
            _live.Start();
        }
    }

    /// <summary>
    /// A seq announced on the live channel. During a run it is only noted (the run's own push comes back this way,
    /// and its pull fetches the rest); the end of the run checks it again.
    /// </summary>
    private void OnLiveSeq(long seq)
    {
        if (_ownSeqs.ContainsKey(seq)) return;
        InterlockedMax(ref _liveSeq, seq);
        if (_run.CurrentCount == 0) return;
        if (seq > _db.GetCursor(MainCursor)) RequestSync();
    }

    private static void InterlockedMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    /// <summary>Syncs now and then every <paramref name="interval"/> (default 5 minutes) while Helm runs.</summary>
    public void Start(TimeSpan? interval = null)
    {
        if (_disposed || _periodic is not null) return;
        var every = interval ?? BackgroundPollInterval;
        _pollInterval = every;
        _periodic = new Timer(_ =>
        {
            if (!_transport.IsConfigured) return;
            // Live: changes are announced, so polling is only a safety net.
            if (IsLive && Environment.TickCount64 - Interlocked.Read(ref _lastRunTicks) < BackgroundPollInterval.TotalMilliseconds) return;
            _ = RunScheduledAsync();
        }, null, TimeSpan.FromSeconds(3), every);
    }

    /// <summary>How often to pull while the app is in front (30 s) or in the background / tray (5 min).</summary>
    public static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan BackgroundPollInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Changes how often the periodic sync pulls (see <see cref="Start"/>), e.g. <see cref="ForegroundPollInterval"/>
    /// while a window is shown so other devices' changes arrive within seconds. Shortening it syncs soon.
    /// </summary>
    public void SetPollInterval(TimeSpan every)
    {
        if (_disposed || _periodic is null || every <= TimeSpan.Zero) return;
        try { _periodic.Change(every < _pollInterval ? TimeSpan.FromSeconds(1) : every, every); }
        catch (ObjectDisposedException) { return; }
        _pollInterval = every;
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
        // The credentials (even the account) may have changed too: forget what the old connection announced, and
        // connect again with the current ones.
        Interlocked.Exchange(ref _liveSeq, 0);
        _ownSeqs.Clear();
        _live?.Reconnect();
    }

    public async Task<SyncRunResult> SyncNowAsync(CancellationToken ct = default)
    {
        if (!_transport.IsConfigured) return NotConfigured();
        var keyring = GetKeyring();
        if (keyring is null) return NotConfigured();

        await _run.WaitAsync(ct).ConfigureAwait(false);
        var run = new RunState();
        var completed = false;
        try
        {
            SetStatus(Status with { State = SyncState.Syncing });
            // Files first: a record that uses a file is pushed only once the file is on the server (I6).
            var blobsFit = _blobs is null || await _blobs.UploadPendingAsync(ct).ConfigureAwait(false);
            await PushAsync(keyring, run, ct).ConfigureAwait(false);
            await PullMainAsync(keyring, run, ct).ConfigureAwait(false);
            await CatchUpAsync(keyring, run, ct).ConfigureAwait(false);
            // Conflicts found while pulling may have kept local edits (rebased or copied): send them now.
            if (run.PendingPush) await PushAsync(keyring, run, ct).ConfigureAwait(false);
            _applyHold = false;
            Hold = null;
            await CollectBlobGarbageAsync(ct).ConfigureAwait(false);
            CollectLocalTombstones();
            if (!blobsFit)
            {
                SetStatus(new SyncStatus(SyncState.QuotaExceeded, _time.GetUtcNow(), "Some files could not be uploaded: storage is full."));
                return run.Result(SyncRunOutcome.QuotaExceeded);
            }
            SetStatus(new SyncStatus(SyncState.Idle, _time.GetUtcNow(), null));
            completed = true;
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
        catch (SyncHeldException ex)
        {
            _logger.LogWarning("Sync held: the server deletes {Count} of {Live} {Collection} records", ex.Hold.Records.Count,
                ex.Hold.LiveRecords, ex.Hold.Collection);
            Hold = ex.Hold;
            SetStatus(Status with { State = SyncState.Held, LastError = ex.Message });
            return run.Result(SyncRunOutcome.Held);
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
            Interlocked.Exchange(ref _lastRunTicks, Environment.TickCount64);
            _run.Release();
            RaiseChanges(run);
            // Something another device announced during the run that the run did not fetch (it came after the pull):
            // go again. Only after a complete run: a held, offline or refused run would otherwise loop.
            if (completed && Interlocked.Read(ref _liveSeq) > _db.GetCursor(MainCursor)) RequestSync();
        }
    }

    /// <summary>The user accepts the held deletions: the next run applies them.</summary>
    public Task<SyncRunResult> ApproveHeldAsync(CancellationToken ct = default)
    {
        if (Hold is { } hold) ResetRecentDeletions(hold.Collection);
        _applyHold = true;
        return SyncNowAsync(ct);
    }

    /// <summary>
    /// The user keeps this device's records: each held deletion is overridden by uploading the local record again on
    /// top of the server's deletion, which restores it on every device.
    /// </summary>
    public async Task<SyncRunResult> RejectHeldAsync(CancellationToken ct = default)
    {
        await _run.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Hold is { } hold)
            {
                _db.InTransaction(() =>
                {
                    foreach (var (id, version) in hold.Records) _db.KeepLocal(hold.Collection, id, version);
                });
                _logger.LogWarning("Kept {Count} {Collection} records that the server had deleted", hold.Records.Count, hold.Collection);
                ResetRecentDeletions(hold.Collection);
                Hold = null;
            }
        }
        finally
        {
            _run.Release();
        }
        return await SyncNowAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_liveGate) _live?.Dispose();
        _timer.Dispose();
        _periodic?.Dispose();
        ReloadKey();
    }

    private async Task PushAsync(SyncKeyring keyring, RunState run, CancellationToken ct)
    {
        for (var round = 0; round < MaxPushRounds; round++)
        {
            // Records whose files are still only on this device wait for the next run, and so do the collections this
            // device does not sync.
            var dirty = _db.GetDirty(PushBatchSize * 10, ExcludedPrefixes).Where(CanPush).Take(PushBatchSize).ToList();
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
                        if (_ownSeqs.Count > 4096) _ownSeqs.Clear();
                        _ownSeqs.TryAdd(outcome.Seq, 0);
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

    /// <summary>
    /// The normal pull from the main cursor, leaving out what this device does not sync. When the server has cleaned
    /// up deletions this device may not have seen, or a previous resync did not finish, everything is pulled again.
    /// </summary>
    private async Task PullMainAsync(SyncKeyring keyring, RunState run, CancellationToken ct)
    {
        var filter = new SyncPullFilter(Exclude: ExcludedPrefixes.Count == 0 ? null : ExcludedPrefixes);
        if (_db.GetMetaValue(ResyncMeta) is null)
        {
            try
            {
                await PullScopeAsync(keyring, run, _db.GetCursor(MainCursor), filter, MainCursor, null, ct).ConfigureAwait(false);
                return;
            }
            catch (SyncResyncRequiredException)
            {
                _logger.LogInformation("The sync server cleaned up deletions this device has not seen; downloading everything again");
                _db.SetMetaValue(ResyncMeta, "1");
            }
        }
        // Null: the selection changed meanwhile; the resync runs again next time (the flag stays).
        if (await ResyncAsync(keyring, run, filter, ct).ConfigureAwait(false) is not { } next)
        {
            RequestSync();
            return;
        }
        _db.InTransaction(() =>
        {
            _db.SetCursor(MainCursor, next);
            _db.SetMetaValue(ResyncMeta, null);
        });
    }

    /// <summary>
    /// Prefixes synced again after a pause: the main cursor went past their records meanwhile, so each is downloaded
    /// again in full (records already known are skipped by version) and what was deleted meanwhile is dropped.
    /// </summary>
    private async Task CatchUpAsync(SyncKeyring keyring, RunState run, CancellationToken ct)
    {
        foreach (var prefix in ParseList(_db.GetMetaValue(CatchUpMeta)))
        {
            var version = Volatile.Read(ref _selectionVersion);
            if (!IsExcluded(prefix) && await ResyncAsync(keyring, run, new SyncPullFilter(Only: [prefix]), ct).ConfigureAwait(false) is null)
            {
                RequestSync();
                continue; // turned off (and maybe on) meanwhile: stays on the list, done again next run
            }
            lock (_selectionGate)
            {
                // Toggled while it ran: keep the entry (turning it on again asked for a fresh catch-up).
                if (Volatile.Read(ref _selectionVersion) != version) continue;
                var left = ParseList(_db.GetMetaValue(CatchUpMeta)).Where(p => p != prefix).ToList();
                _db.SetMetaValue(CatchUpMeta, left.Count == 0 ? null : string.Join(',', left));
            }
        }
    }

    /// <summary>
    /// Pulls every record of <paramref name="filter"/> from seq 0 and then drops the local rows the server no longer
    /// has (their deletions were cleaned up there). Progress is not saved: the comparison needs the complete list, so an
    /// interrupted resync starts over. Returns the seq to continue from.
    /// </summary>
    /// <returns>Null when this device's selection changed during the pull: then nothing is dropped (records skipped
    /// while a prefix was off would look missing), and the caller tries again.</returns>
    private async Task<long?> ResyncAsync(SyncKeyring keyring, RunState run, SyncPullFilter filter, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var seen = new HashSet<(string, string)>();
            var version = Volatile.Read(ref _selectionVersion);
            try
            {
                var next = await PullScopeAsync(keyring, run, 0, filter, null, seen, ct).ConfigureAwait(false);
                if (Volatile.Read(ref _selectionVersion) != version) return null;
                DropMissing(filter, seen, run);
                return next;
            }
            catch (SyncResyncRequiredException) when (attempt < MaxResyncAttempts)
            {
                // Another clean-up ran on the server while pulling: the list is not complete, start over.
            }
        }
    }

    /// <summary>
    /// After a complete resync: a local row with a server version that the server did not return was deleted and
    /// cleaned up there. A clean row goes (guarded collections may hold the run, as for any mass deletion); a local
    /// edit of it is kept and uploaded again as a new record, since an edit beats a deletion.
    /// </summary>
    private void DropMissing(SyncPullFilter filter, HashSet<(string, string)> seen, RunState run)
    {
        var missing = _db.ListKeys()
            .Where(k => k.Version > 0 && filter.Matches(k.Collection) && !IsExcluded(k.Collection) && !seen.Contains((k.Collection, k.Id)))
            .ToList();
        if (missing.Count == 0) return;

        var removed = new List<(string Collection, string Id)>();
        foreach (var (collection, id, _, deleted, dirty) in missing)
        {
            if (dirty && !deleted)
            {
                _db.Rebase(collection, id, 0);
                run.PendingPush = true;
                continue;
            }
            if (!deleted && _descriptors.TryGetValue(collection, out var descriptor) && descriptor.ChangeGuard is { } guard)
            {
                var local = _db.Get(collection, id);
                if (local?.Body is null || guard.IsExpendable is not { } expendable || !IsExpendable(expendable, local.Body))
                {
                    if (!run.GuardedDeletions.TryGetValue(collection, out var deletions)) run.GuardedDeletions[collection] = deletions = [];
                    deletions.Add((id, 0));
                }
            }
            removed.Add((collection, id));
        }
        if (!_applyHold) CheckChangeGuards(run);
        _db.InTransaction(() =>
        {
            foreach (var (collection, id) in removed) _db.Remove(collection, id);
        });
        foreach (var (collection, deletions) in run.GuardedDeletions) AddRecentDeletions(collection, deletions.Count);
        run.GuardedDeletions.Clear();
        foreach (var (collection, id) in removed) run.Changed(collection, id);
        _logger.LogInformation("Resync dropped {Count} records the server no longer has", removed.Count);
    }

    /// <param name="cursor">Saved after every applied page; null for a resync, which must not save partial progress.</param>
    /// <param name="seen">Collects every record the server returned (for a resync's comparison).</param>
    /// <returns>The seq to continue from.</returns>
    private async Task<long> PullScopeAsync(SyncKeyring keyring, RunState run, long since, SyncPullFilter filter, string? cursor,
        HashSet<(string, string)>? seen, CancellationToken ct)
    {
        // Pages that delete guarded records are collected and judged together before any of them is applied, so
        // deletions spread over several pages cannot slip under the threshold one page at a time.
        var pending = new List<(PullPage Page, long Next)>();
        while (true)
        {
            var page = await _transport.PullAsync(since, PullPageSize, filter, ct).ConfigureAwait(false);
            var next = Math.Max(since, page.NextSeq);
            var last = !page.HasMore || next == since;
            if (_applyHold)
            {
                Apply(keyring, page, next, run, filter, cursor, seen);
            }
            else
            {
                pending.Add((page, next));
                CollectGuardedDeletions(page.Records.Where(r => filter.Matches(r.Collection) && !IsExcluded(r.Collection)).ToList(), run);
                // Once a guarded deletion was seen, keep collecting to the end of the pull (or the memory cap).
                if (run.GuardedDeletions.Count == 0 || last || pending.Count >= MaxHeldPages)
                {
                    // Throws before anything pending is applied; the cursor stays, so the same pages come back next run.
                    CheckChangeGuards(run);
                    foreach (var (held, heldNext) in pending) Apply(keyring, held, heldNext, run, filter, cursor, seen);
                    foreach (var (collection, deletions) in run.GuardedDeletions) AddRecentDeletions(collection, deletions.Count);
                    pending.Clear();
                    run.GuardedDeletions.Clear();
                }
            }
            if (last) return next;
            since = next;
        }
    }

    private void Apply(SyncKeyring keyring, PullPage page, long next, RunState run, SyncPullFilter filter, string? cursor,
        HashSet<(string, string)>? seen) => _db.InTransaction(() =>
    {
        foreach (var record in page.Records)
        {
            // An older server ignores the filter: leave out what this device does not sync here.
            if (!filter.Matches(record.Collection) || IsExcluded(record.Collection)) continue;
            seen?.Add((record.Collection, record.Id));
            ApplyPulled(keyring, record, run);
        }
        if (cursor is not null) _db.SetCursor(cursor, next);
    });

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

    private bool CanPush(SyncRow row)
    {
        if (_blobs is null || row.Deleted || row.Body is null) return true;
        if (!_descriptors.TryGetValue(row.Collection, out var descriptor) || descriptor.BlobReferences is not { } references) return true;
        try
        {
            return !references(row.Body).Any(_blobs.IsPending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the files of {Collection}/{Id}; pushing it anyway", row.Collection, row.Id);
            return true;
        }
    }

    /// <summary>
    /// At most daily, after a complete run: blobs no record uses are deleted (after a grace period, softly on the
    /// server) and used ones that were deleted are restored. Skipped whenever the replica holds a collection this build
    /// does not know, because its records might use blobs nobody here can see.
    /// </summary>
    private async Task CollectBlobGarbageAsync(CancellationToken ct)
    {
        if (_blobs is null) return;
        var last = long.TryParse(_db.GetMetaValue(BlobGcMeta), out var ms) ? ms : 0;
        if (_time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(last) < BlobGcInterval) return;
        try
        {
            var unknown = _db.ListCollections().Where(c => !_descriptors.ContainsKey(c)).ToList();
            if (unknown.Count > 0)
            {
                _logger.LogInformation("Blob clean-up skipped: unknown collections {Collections}", string.Join(", ", unknown));
                return;
            }
            // Collections this device does not sync (or still has to download again) are out of date here: their newer
            // records may use blobs it never saw.
            if (_db.GetMetaValue(CatchUpMeta) is not null || _db.GetMetaValue(ResyncMeta) is not null)
            {
                _logger.LogInformation("Blob clean-up skipped: a download of everything is still pending");
                return;
            }
            if (ExcludedPrefixes.Count > 0)
            {
                _logger.LogInformation("Blob clean-up skipped: this device does not sync {Prefixes}", string.Join(", ", ExcludedPrefixes));
                return;
            }
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var descriptor in _descriptors.Values)
            {
                if (descriptor.BlobReferences is not { } references) continue;
                foreach (var row in _db.List(descriptor.Name))
                {
                    if (row.Body is not null) referenced.UnionWith(references(row.Body));
                }
            }
            await _blobs.CollectGarbageAsync(referenced, ct).ConfigureAwait(false);
            _db.SetMetaValue(BlobGcMeta, _time.GetUtcNow().ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A hook that cannot read a record, or the server failing: clean up another day, never guess.
            _logger.LogWarning(ex, "Blob clean-up failed; it runs again later");
        }
    }

    /// <summary>
    /// At most daily: local deletions the server accepted long ago are forgotten, so the replica does not keep one row
    /// for every record ever deleted.
    /// </summary>
    private void CollectLocalTombstones()
    {
        var last = long.TryParse(_db.GetMetaValue(TombstoneGcMeta), out var ms) ? ms : 0;
        var now = _time.GetUtcNow();
        if (now - DateTimeOffset.FromUnixTimeMilliseconds(last) < BlobGcInterval) return;
        var purged = _db.PurgeTombstones((now - LocalTombstoneRetention).ToUnixTimeMilliseconds());
        _db.SetMetaValue(TombstoneGcMeta, now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (purged > 0) _logger.LogInformation("Forgot {Count} old local deletions", purged);
    }

    private static string[] ParseList(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void CollectGuardedDeletions(IReadOnlyList<RemoteRecord> records, RunState run)
    {
        foreach (var record in records)
        {
            if (!record.Deleted || !_descriptors.TryGetValue(record.Collection, out var descriptor) || descriptor.ChangeGuard is not { } guard) continue;
            var local = _db.Get(record.Collection, record.Id);
            if (local is null || local.Deleted || local.Version >= record.Version) continue;
            if (local.Body is not null && guard.IsExpendable is { } expendable && IsExpendable(expendable, local.Body)) continue;
            if (!run.GuardedDeletions.TryGetValue(record.Collection, out var deletions))
                run.GuardedDeletions[record.Collection] = deletions = [];
            deletions.Add((record.Id, record.Version));
        }
    }

    /// <summary>
    /// Deletions already applied in the last <see cref="GuardWindow"/> count too, so a hostile or buggy source cannot
    /// empty a collection a few records per run, or by padding one run with thousands of other records.
    /// </summary>
    private void CheckChangeGuards(RunState run)
    {
        foreach (var (collection, deletions) in run.GuardedDeletions)
        {
            var live = _db.List(collection).Count;
            var recent = RecentDeletions(collection);
            if (recent + deletions.Count >= SyncChangeGuard.Threshold(live + recent))
                throw new SyncHeldException(new SyncHold(collection, live, deletions.ToList()));
        }
    }

    private static readonly TimeSpan GuardWindow = TimeSpan.FromHours(24);

    private int RecentDeletions(string collection)
    {
        var stored = _db.GetMetaValue(GuardMeta(collection))?.Split('|');
        if (stored is not [var start, var count] || !long.TryParse(start, out var startMs) || !int.TryParse(count, out var n)) return 0;
        return _time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(startMs) > GuardWindow ? 0 : n;
    }

    private void AddRecentDeletions(string collection, int count)
    {
        if (count == 0) return;
        var recent = RecentDeletions(collection);
        var stored = _db.GetMetaValue(GuardMeta(collection))?.Split('|');
        var start = recent > 0 && stored is [var s, _] ? s : _time.GetUtcNow().ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        _db.SetMetaValue(GuardMeta(collection), $"{start}|{recent + count}");
    }

    /// <summary>The user decided about a hold: what was deleted so far no longer counts against the next one.</summary>
    private void ResetRecentDeletions(string collection) => _db.SetMetaValue(GuardMeta(collection), null);

    private static string GuardMeta(string collection) => "guard_deletions:" + collection;

    private bool IsExpendable(Func<string, bool> expendable, string body)
    {
        try
        {
            return expendable(body);
        }
        catch (Exception ex)
        {
            // Unknown means guarded: a hook that cannot read the body must not let deletions through.
            _logger.LogWarning(ex, "A change-guard hook failed");
            return false;
        }
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
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        try { StatusChanged?.Invoke(this, Status); }
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
        /// <summary>Guarded deletions in the pages pulled but not applied yet.</summary>
        public Dictionary<string, List<(string Id, long Version)>> GuardedDeletions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> ChangedIds { get; } = new(StringComparer.Ordinal);

        public void Changed(string collection, string id)
        {
            if (!ChangedIds.TryGetValue(collection, out var ids)) ChangedIds[collection] = ids = new HashSet<string>(StringComparer.Ordinal);
            ids.Add(id);
        }

        public SyncRunResult Result(SyncRunOutcome outcome) => new(outcome, Pushed, Pulled, Conflicts);
    }
}
