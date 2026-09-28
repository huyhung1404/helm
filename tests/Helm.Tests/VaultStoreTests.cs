using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Tests;

public sealed class VaultStoreTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();
    private readonly byte[] _syncKey = SyncKeyring.CreateMasterKey();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task A_new_vault_is_unlocked_and_items_round_trip()
    {
        var a = NewDevice("a");
        var recovery = await a.Session.CreateAsync(Password);
        Assert.StartsWith("HELMV-", recovery);
        Assert.Equal(VaultState.Unlocked, a.Session.State);

        var uid = a.Store.Add(Login("Bank", "hunter2"));
        var entry = Assert.Single(a.Store.Items());
        Assert.Equal(uid, entry.Uid);
        Assert.Equal("hunter2", PasswordOf(entry.Item));
    }

    [Fact]
    public async Task Locked_vaults_refuse_reads_and_writes_and_forget_decrypted_items()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        a.Store.Add(Login("Bank", "hunter2"));
        a.Session.Lock("test");

        Assert.Equal(VaultState.Locked, a.Session.State);
        Assert.Throws<VaultLockedException>(() => a.Store.Items());
        Assert.Throws<VaultLockedException>(() => a.Store.Add(Login("x", "y")));
        Assert.Equal(1, a.Store.RecordCount);

        await a.Session.UnlockAsync(Password);
        Assert.Equal("hunter2", PasswordOf(Assert.Single(a.Store.Items()).Item));
    }

    [Fact]
    public async Task The_replica_never_holds_item_content_in_clear()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        a.Store.Add(Login("Bank", "hunter2-plaintext-marker"));
        var record = Assert.Single(a.Records.All()).Value;
        Assert.DoesNotContain("hunter2", System.Text.Encoding.UTF8.GetString(record.Data));
        Assert.DoesNotContain("Bank", System.Text.Encoding.UTF8.GetString(record.Data));
    }

    [Fact]
    public async Task Every_edit_keeps_the_previous_versions_up_to_ten()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        var uid = a.Store.Add(Login("Bank", "v0"));
        for (var i = 1; i <= 12; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            a.Store.Save(uid, Login("Bank", $"v{i}"));
        }

        var item = a.Store.Get(uid)!.Item;
        Assert.Equal("v12", PasswordOf(item));
        Assert.Equal(VaultItem.MaxHistory, item.History.Count);
        Assert.Equal(Enumerable.Range(2, 10).Reverse().Select(i => $"v{i}"), item.History.Select(h => PasswordOf(h.Item)));
        Assert.All(item.History, h => Assert.Empty(h.Item.History));
    }

    [Fact]
    public async Task Saving_the_same_content_adds_no_version_and_restoring_a_version_is_itself_undoable()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        var uid = a.Store.Add(Login("Bank", "old"));
        a.Store.Save(uid, Login("Bank", "old"));
        Assert.Empty(a.Store.Get(uid)!.Item.History);

        _clock.Advance(TimeSpan.FromMinutes(1));
        a.Store.Save(uid, Login("Bank", "new"));
        var old = Assert.Single(a.Store.Get(uid)!.Item.History);
        _clock.Advance(TimeSpan.FromMinutes(1));
        a.Store.RestoreVersion(uid, old);

        var item = a.Store.Get(uid)!.Item;
        Assert.Equal("old", PasswordOf(item));
        Assert.Equal("new", PasswordOf(Assert.Single(item.History).Item));
    }

    [Fact]
    public async Task Items_leave_only_through_the_trash()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        var uid = a.Store.Add(Login("Bank", "x"));

        Assert.Throws<InvalidOperationException>(() => a.Store.Purge(uid));
        a.Store.MoveToTrash(uid);
        Assert.Empty(a.Store.Items());
        Assert.True(Assert.Single(a.Store.Trash()).Trashed);

        a.Store.RestoreFromTrash(uid);
        Assert.Single(a.Store.Items());
        a.Store.MoveToTrash(uid);
        a.Store.Purge(uid);
        Assert.Empty(a.Store.Trash());
        Assert.Equal(0, a.Store.RecordCount);
    }

    [Fact]
    public async Task The_trash_is_emptied_after_the_retention_even_while_locked()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        var old = a.Store.Add(Login("Old", "x"));
        var recent = a.Store.Add(Login("Recent", "x"));
        a.Store.MoveToTrash(old);
        _clock.Advance(TimeSpan.FromDays(20));
        Assert.Equal(VaultState.Locked, a.Session.State);
        await a.Session.UnlockAsync(Password);
        a.Store.MoveToTrash(recent);
        _clock.Advance(TimeSpan.FromDays(11));
        a.Session.Lock("test");

        Assert.Equal(1, a.Store.PurgeExpired(TimeSpan.FromDays(30)));
        await a.Session.UnlockAsync(Password);
        Assert.Equal("Recent", Assert.Single(a.Store.Trash()).Item.Title);
    }

    [Fact]
    public async Task A_second_device_unlocks_the_same_vault_with_the_password()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        await a.Session.CreateAsync(Password);
        var uid = a.Store.Add(Login("Bank", "hunter2"));
        await Sync(a, b);

        Assert.Equal(VaultState.Locked, b.Session.State);
        await Assert.ThrowsAsync<VaultKeyException>(() => b.Session.UnlockAsync("wrong password, surely"));
        await b.Session.UnlockAsync(Password);
        Assert.Equal("hunter2", PasswordOf(b.Store.Get(uid)!.Item));
    }

    [Fact]
    public async Task Creating_a_vault_syncs_first_and_refuses_a_second_vault()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        await a.Session.CreateAsync(Password);
        await Sync(a);

        // B never synced, but CreateAsync syncs before creating anything.
        await Assert.ThrowsAsync<VaultKeyException>(() => b.Session.CreateAsync("another good vault password"));
        Assert.Equal(VaultState.Locked, b.Session.State);
        Assert.Single(b.Keyrings.All());
    }

    [Fact]
    public async Task Creating_a_vault_while_the_server_is_unreachable_is_refused()
    {
        var transport = _server.Connect();
        var a = NewDevice("a", transport);
        transport.Offline = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Session.CreateAsync(Password));
        Assert.Equal(VaultState.NotSetUp, a.Session.State);
    }

    [Fact]
    public async Task Concurrent_edits_become_a_conflict_and_resolving_keeps_the_other_version_in_history()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        await a.Session.CreateAsync(Password);
        var uid = a.Store.Add(Login("Bank", "base"));
        await Sync(a, b);
        await b.Session.UnlockAsync(Password);

        _clock.Advance(TimeSpan.FromMinutes(1));
        a.Store.Save(uid, Login("Bank", "from a"));
        b.Store.Save(uid, Login("Bank", "from b"));
        await Sync(a, b, a);

        foreach (var device in new[] { a, b })
        {
            var entry = Assert.Single(device.Store.Items());
            var conflict = Assert.Single(entry.Conflicts);
            Assert.Equal(new[] { "from a", "from b" }, new[] { PasswordOf(entry.Item), PasswordOf(conflict.Item) }.Order());
        }

        var toKeep = a.Store.Get(uid)!.Conflicts[0];
        a.Store.ResolveConflict(uid, toKeep.RecordId);
        await Sync(a, b);

        foreach (var device in new[] { a, b })
        {
            var entry = Assert.Single(device.Store.Items());
            Assert.Empty(entry.Conflicts);
            Assert.Equal(PasswordOf(toKeep.Item), PasswordOf(entry.Item));
            var history = entry.Item.History.Select(h => PasswordOf(h.Item)).ToList();
            Assert.Contains(PasswordOf(toKeep.Item) == "from a" ? "from b" : "from a", history);
            Assert.Contains("base", history);
        }
    }

    [Fact]
    public async Task Records_that_do_not_open_are_reported_not_hidden_or_lost()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        a.Store.Add(Login("Good", "x"));
        using var foreign = VaultKey.Create();
        a.Records.Upsert("forged", VaultItemSealer.Seal(foreign, a.Session.VaultId!, 1, "forged", Login("Bad", "x"), false, null));
        a.Records.Upsert("elsewhere", VaultItemSealer.Seal(foreign, "other-vault", 1, "elsewhere", Login("Other", "x"), false, null));

        Assert.Equal("Good", Assert.Single(a.Store.Items()).Item.Title);
        var problems = a.Store.Unreadable();
        Assert.Equal(["elsewhere", "forged"], problems.Select(p => p.RecordId).Order());
        Assert.Equal(3, a.Store.RecordCount);
    }

    [Fact]
    public async Task Bulk_deletions_from_another_device_are_held_but_emptying_the_trash_is_not()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        await a.Session.CreateAsync(Password);
        var uids = Enumerable.Range(0, 10).Select(i => a.Store.Add(Login($"item {i}", "x"))).ToList();
        foreach (var uid in uids.Take(5)) a.Store.MoveToTrash(uid);
        await Sync(a, b);

        foreach (var uid in uids.Take(5)) a.Store.Purge(uid);
        await Sync(a, b);
        Assert.Equal(5, b.Store.RecordCount);

        // Records deleted without going through the trash (a bug, or a tampered device) are held on B.
        foreach (var uid in uids.Skip(5)) a.Records.Delete(uid);
        await Sync(a);
        Assert.Equal(SyncRunOutcome.Held, (await b.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(5, b.Store.RecordCount);
        await b.Engine.RejectHeldAsync();
        await Sync(a);
        await b.Session.UnlockAsync(Password);
        Assert.Equal(5, b.Store.Items().Count);
        Assert.Equal(5, a.Store.Items().Count);
    }

    [Fact]
    public async Task Wrong_passwords_are_throttled_after_three_attempts()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        a.Session.Lock("test");
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<VaultKeyException>(() => a.Session.UnlockAsync("definitely the wrong one"));

        var throttled = await Assert.ThrowsAsync<VaultThrottledException>(() => a.Session.UnlockAsync(Password));
        Assert.True(throttled.RetryAfter > TimeSpan.Zero);
        _clock.Advance(TimeSpan.FromSeconds(3));
        await a.Session.UnlockAsync(Password);
        Assert.Equal(VaultState.Unlocked, a.Session.State);
    }

    [Fact]
    public async Task The_vault_locks_itself_after_inactivity()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        var locked = 0;
        a.Session.Locking += (_, _) => locked++;

        _clock.Advance(TimeSpan.FromMinutes(4));
        a.Session.Touch();
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(VaultState.Unlocked, a.Session.State);
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(VaultState.Locked, a.Session.State);
        Assert.Equal(1, locked);
    }

    [Fact]
    public async Task The_recovery_key_unlocks_and_forces_a_new_password()
    {
        var a = NewDevice("a");
        var recovery = await a.Session.CreateAsync(Password);
        a.Session.Lock("test");

        await a.Session.UnlockWithRecoveryKeyAsync(recovery.ToLowerInvariant());
        Assert.True(a.Session.MustChangePassword);
        await a.Session.ChangePasswordAsync(null, "my new vault password 42");
        Assert.False(a.Session.MustChangePassword);

        a.Session.Lock("test");
        await Assert.ThrowsAsync<VaultKeyException>(() => a.Session.UnlockAsync(Password));
        await a.Session.UnlockAsync("my new vault password 42");
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        await Assert.ThrowsAsync<VaultKeyException>(() => a.Session.ChangePasswordAsync("not the password at all", "my new vault password 42"));
        await a.Session.ChangePasswordAsync(Password, "my new vault password 42");
    }

    [Fact]
    public async Task The_emergency_kit_is_confirmed_by_typing_the_current_recovery_key()
    {
        var a = NewDevice("a");
        var recovery = await a.Session.CreateAsync(Password);
        Assert.False(a.Session.RecoveryKitConfirmed);
        Assert.False(a.Session.ConfirmRecoveryKit("HELMV-0000"));
        Assert.True(a.Session.ConfirmRecoveryKit(recovery));
        Assert.True(a.Session.RecoveryKitConfirmed);

        var replaced = await a.Session.NewRecoveryKeyAsync(Password);
        Assert.False(a.Session.RecoveryKitConfirmed);
        Assert.False(a.Session.ConfirmRecoveryKit(recovery));
        Assert.True(a.Session.ConfirmRecoveryKit(replaced));
    }

    [Fact]
    public async Task Quick_unlock_works_until_the_password_is_due_again()
    {
        var unlock = new FakeDeviceUnlock();
        var a = NewDevice("a", deviceUnlock: unlock);
        await a.Session.CreateAsync(Password);
        Assert.True(await a.Session.EnableDeviceUnlockAsync());
        a.Session.Lock("test");

        Assert.True(a.Session.CanUseDeviceUnlock);
        Assert.True(await a.Session.UnlockWithDeviceAsync());
        Assert.Equal(VaultState.Unlocked, a.Session.State);

        a.Session.Lock("test");
        _clock.Advance(TimeSpan.FromDays(15));
        Assert.False(a.Session.CanUseDeviceUnlock);
        Assert.False(await a.Session.UnlockWithDeviceAsync());
        await a.Session.UnlockAsync(Password);
        a.Session.Lock("test");
        Assert.True(a.Session.CanUseDeviceUnlock);
    }

    [Fact]
    public async Task Quick_unlock_stops_after_repeated_cancels_and_refuses_a_foreign_key()
    {
        var unlock = new FakeDeviceUnlock();
        var a = NewDevice("a", deviceUnlock: unlock);
        await a.Session.CreateAsync(Password);
        await a.Session.EnableDeviceUnlockAsync();
        a.Session.Lock("test");

        unlock.Cancel = true;
        for (var i = 0; i < VaultSession.MaxDeviceUnlockFailures; i++) Assert.False(await a.Session.UnlockWithDeviceAsync());
        Assert.False(a.Session.CanUseDeviceUnlock);
        await a.Session.UnlockAsync(Password);
        a.Session.Lock("test");

        unlock.Cancel = false;
        unlock.Stored = VaultKey.Create().Bytes.ToArray();
        await Assert.ThrowsAsync<VaultDeviceUnlockException>(() => a.Session.UnlockWithDeviceAsync());
        Assert.Equal(VaultState.Locked, a.Session.State);
        Assert.False(a.Session.IsDeviceUnlockEnrolled);
    }

    private static VaultItem Login(string title, string password) => VaultItem.New(VaultItemKind.Login, title) with
    {
        Fields = [new VaultField("Username", "anh"), new VaultField("Password", password, VaultFieldKind.Password)],
    };

    private static string PasswordOf(VaultItem item) => item.Fields.Single(f => f.Kind == VaultFieldKind.Password).Value;

    private sealed record Device(
        SyncEngine Engine, VaultSession Session, VaultStore Store,
        SyncedCollection<VaultItemRecord> Records, SyncedCollection<VaultKeyringData> Keyrings);

    private Device NewDevice(string name, FakeSyncServer.Transport? transport = null, IVaultDeviceUnlock? deviceUnlock = null)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var engine = Own(new SyncEngine(db, transport ?? _server.Connect(), new InMemoryMasterKeyStore(_syncKey), [], time: _clock,
            debounce: TimeSpan.FromHours(1)));
        var records = Own(new SyncedCollection<VaultItemRecord>(engine, VaultStore.Options));
        var keyrings = Own(new SyncedCollection<VaultKeyringData>(engine, new SyncedCollectionOptions<VaultKeyringData>
        {
            Name = VaultSession.KeyringCollection,
        }));
        var settings = Own(new SettingsStoreFactory(paths));
        var session = Own(new VaultSession(keyrings, settings, deviceUnlock ?? new NoDeviceUnlock(), engine, _clock)
        {
            NewKdf = VaultCryptoTests.CheapKdf,
        });
        var store = Own(new VaultStore(records, session, _clock));
        return new Device(engine, session, store, records, keyrings);
    }

    private static async Task Sync(params Device[] devices)
    {
        foreach (var device in devices) Assert.Equal(SyncRunOutcome.Completed, (await device.Engine.SyncNowAsync()).Outcome);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private sealed class FakeDeviceUnlock : IVaultDeviceUnlock
    {
        public byte[]? Stored { get; set; }
        public bool Cancel { get; set; }
        public string Name => "Fake";
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);
        public bool IsEnrolled(string vaultId) => Stored is not null;

        public Task<bool> EnrollAsync(string vaultId, ReadOnlyMemory<byte> vaultKey, CancellationToken ct)
        {
            Stored = vaultKey.ToArray();
            return Task.FromResult(true);
        }

        public Task<byte[]?> UnlockAsync(string vaultId, CancellationToken ct) => Task.FromResult(Cancel ? null : Stored?.ToArray());

        public void Remove() => Stored = null;
    }
}

/// <summary>A clock whose timers fire when <see cref="Advance"/> passes their due time.</summary>
internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private readonly List<Timer> _timers = [];

    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_timers) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = Now + by;
        while (true)
        {
            Timer? next;
            lock (_timers) next = _timers.Where(t => t.Due is not null && t.Due <= target).MinBy(t => t.Due);
            if (next is null) break;
            Now = next.Due!.Value;
            next.Fire();
        }
        Now = target;
    }

    private sealed class Timer(FakeClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : clock.Now + _period;
            callback(state);
        }

        public void Dispose()
        {
            Due = null;
            lock (clock._timers) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
