using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>
/// Selective sync per device, the server's clean-up of old deletions (tombstone GC) with the resync it forces on a
/// device that was away longer, and local tombstone clean-up. Runs against <see cref="FakeSyncServer"/>.
/// </summary>
public sealed class SyncSelectiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();
    private readonly byte[] _key = SyncKeyring.CreateMasterKey();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> _owned = [];

    public SyncSelectiveTests() => _server.Now = () => _time.Now;

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task A_device_that_does_not_sync_a_tool_neither_uploads_nor_downloads_it_and_catches_up_when_turned_back_on()
    {
        var pc = NewDevice("pc");
        var phone = NewDevice("phone");
        phone.Engine.SetExcluded("vault.", true);

        pc.Notes.Upsert("n1", new Note("Note", "from PC"));
        pc.Secrets.Upsert("s1", new Note("Secret", "from PC"));
        phone.Secrets.Upsert("local", new Note("Phone secret", "stays here"));
        await SyncAll(pc, phone, pc);

        Assert.Equal(new Note("Note", "from PC"), phone.Notes.Get("n1"));
        Assert.Null(phone.Secrets.Get("s1"));
        Assert.Null(pc.Secrets.Get("local"));
        // It asks the server to leave the tool out, and the filter is not only client-side.
        Assert.Contains(_server.PullFilters, f => f.Exclude is ["vault."]);

        // Meanwhile the PC edits and deletes vault items.
        pc.Secrets.Upsert("s2", new Note("Another", "while the phone skipped the vault"));
        await SyncAll(pc, phone);

        phone.Engine.SetExcluded("vault.", false);
        await SyncAll(phone, pc);
        Assert.Equal(new Note("Secret", "from PC"), phone.Secrets.Get("s1"));
        Assert.Equal(new Note("Another", "while the phone skipped the vault"), phone.Secrets.Get("s2"));
        // The edit made while it was off is uploaded now.
        Assert.Equal(new Note("Phone secret", "stays here"), pc.Secrets.Get("local"));
        Assert.Empty(phone.Engine.ExcludedPrefixes);
    }

    [Fact]
    public async Task Re_enabling_drops_records_deleted_elsewhere_whose_deletion_the_server_already_cleaned_up()
    {
        var pc = NewDevice("pc");
        var phone = NewDevice("phone");
        pc.Secrets.Upsert("gone", new Note("Deleted later", "x"));
        pc.Secrets.Upsert("kept", new Note("Kept", "y"));
        await SyncAll(pc, phone);
        phone.Engine.SetExcluded("vault.", true);

        Assert.True(pc.Secrets.Delete("gone"));
        await SyncAll(pc);
        _time.Advance(TimeSpan.FromDays(100));
        Assert.Equal(1, _server.PurgeTombstones(_time.Now - TimeSpan.FromDays(90)));

        phone.Engine.SetExcluded("vault.", false);
        await SyncAll(phone);
        Assert.Null(phone.Secrets.Get("gone"));
        Assert.Equal(new Note("Kept", "y"), phone.Secrets.Get("kept"));
    }

    [Fact]
    public async Task A_device_away_longer_than_the_retention_resyncs_and_drops_what_was_deleted_meanwhile()
    {
        var pc = NewDevice("pc");
        var laptop = NewDevice("laptop");
        pc.Notes.Upsert("a", new Note("A", "1"));
        pc.Notes.Upsert("b", new Note("B", "2"));
        pc.Notes.Upsert("c", new Note("C", "3"));
        await SyncAll(pc, laptop);

        // While the laptop is away: b is deleted, the laptop edits c offline, and the deletion is cleaned up.
        Assert.True(pc.Notes.Delete("b"));
        Assert.True(pc.Notes.Delete("c"));
        pc.Notes.Upsert("d", new Note("D", "new"));
        await SyncAll(pc);
        laptop.Notes.Upsert("c", new Note("C", "edited offline"));
        _time.Advance(TimeSpan.FromDays(100));
        Assert.Equal(2, _server.PurgeTombstones(_time.Now - TimeSpan.FromDays(90)));

        await SyncAll(laptop, pc);
        Assert.Null(laptop.Notes.Get("b"));
        Assert.Equal(new Note("D", "new"), laptop.Notes.Get("d"));
        Assert.Equal(new Note("A", "1"), laptop.Notes.Get("a"));
        // An edit beats a deletion: the offline edit comes back as a record on both devices.
        Assert.Equal(new Note("C", "edited offline"), laptop.Notes.Get("c"));
        Assert.Equal(new Note("C", "edited offline"), pc.Notes.Get("c"));

        // Back to normal pulls afterwards.
        pc.Notes.Upsert("e", new Note("E", "after"));
        await SyncAll(pc, laptop);
        Assert.Equal(new Note("E", "after"), laptop.Notes.Get("e"));
    }

    [Fact]
    public async Task A_resync_that_would_delete_many_guarded_records_is_held_for_the_user()
    {
        var pc = NewDevice("pc");
        var laptop = NewDevice("laptop");
        for (var i = 0; i < 10; i++) pc.Secrets.Upsert($"s{i}", new Note($"Secret {i}", "x"));
        await SyncAll(pc, laptop);

        for (var i = 0; i < 5; i++) Assert.True(pc.Secrets.Delete($"s{i}"));
        await SyncAll(pc);
        _time.Advance(TimeSpan.FromDays(100));
        _server.PurgeTombstones(_time.Now - TimeSpan.FromDays(90));

        Assert.Equal(SyncRunOutcome.Held, (await laptop.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(10, laptop.Secrets.All().Count);
        Assert.Equal(5, laptop.Engine.Hold!.Records.Count);

        // Keeping them uploads them again, as new records.
        Assert.Equal(SyncRunOutcome.Completed, (await laptop.Engine.RejectHeldAsync()).Outcome);
        await SyncAll(pc);
        Assert.Equal(10, pc.Secrets.All().Count);
    }

    [Fact]
    public async Task Approving_a_held_resync_drops_the_records()
    {
        var pc = NewDevice("pc");
        var laptop = NewDevice("laptop");
        for (var i = 0; i < 10; i++) pc.Secrets.Upsert($"s{i}", new Note($"Secret {i}", "x"));
        await SyncAll(pc, laptop);
        for (var i = 0; i < 5; i++) Assert.True(pc.Secrets.Delete($"s{i}"));
        await SyncAll(pc);
        _time.Advance(TimeSpan.FromDays(100));
        _server.PurgeTombstones(_time.Now - TimeSpan.FromDays(90));

        Assert.Equal(SyncRunOutcome.Held, (await laptop.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(SyncRunOutcome.Completed, (await laptop.Engine.ApproveHeldAsync()).Outcome);
        Assert.Equal(5, laptop.Secrets.All().Count);
    }

    [Fact]
    public async Task A_server_that_ignores_filters_still_leaves_the_excluded_tool_alone()
    {
        var pc = NewDevice("pc");
        var old = _server.Connect();
        old.IgnoresFilters = true;
        var phone = NewDevice("phone", old);
        phone.Engine.SetExcluded("vault.", true);

        pc.Secrets.Upsert("s1", new Note("Secret", "x"));
        await SyncAll(pc, phone);
        Assert.Null(phone.Secrets.Get("s1"));

        phone.Engine.SetExcluded("vault.", false);
        await SyncAll(phone);
        Assert.Equal(new Note("Secret", "x"), phone.Secrets.Get("s1"));
    }

    [Fact]
    public async Task Local_deletions_are_forgotten_after_the_retention_but_recent_ones_are_kept()
    {
        var path = Path.Combine(_dir, "pc.db");
        var db = Own(new SyncDatabase(path, TestKeys.Local));
        var engine = Own(NewEngine(db, _server.Connect()));
        var notes = Own(new SyncedCollection<Note>(engine, new() { Name = "notes.items" }));
        notes.Upsert("old", new Note("Old", "x"));
        await engine.SyncNowAsync();
        Assert.True(notes.Delete("old"));
        await engine.SyncNowAsync();
        Assert.NotNull(db.Get("notes.items", "old"));

        _time.Advance(SyncEngine.LocalTombstoneRetention + TimeSpan.FromDays(2));
        notes.Upsert("recent", new Note("R", "y"));
        Assert.True(notes.Delete("recent"));
        Assert.Equal(SyncRunOutcome.Completed, (await engine.SyncNowAsync()).Outcome);
        Assert.Null(db.Get("notes.items", "old"));
        Assert.True(db.Get("notes.items", "recent")!.Deleted);
    }

    [Fact]
    public void Only_prefixes_can_be_excluded()
    {
        var device = NewDevice("pc");
        Assert.Throws<ArgumentException>(() => device.Engine.SetExcluded("vault", true));
        device.Engine.SetExcluded("vault.", true);
        device.Engine.SetExcluded("vault.", true);
        Assert.Equal(["vault."], device.Engine.ExcludedPrefixes);
        Assert.True(device.Engine.IsExcluded("vault.items"));
        Assert.False(device.Engine.IsExcluded("vaultx.items"));
    }

    [Fact]
    public void The_selection_survives_a_restart()
    {
        var path = Path.Combine(_dir, "pc.db");
        using (var db = new SyncDatabase(path, TestKeys.Local))
        using (var engine = NewEngine(db, _server.Connect()))
        {
            engine.SetExcluded("tracker.", true);
        }
        using var reopened = new SyncDatabase(path, TestKeys.Local);
        using var again = NewEngine(reopened, _server.Connect());
        Assert.Equal(["tracker."], again.ExcludedPrefixes);
    }

    private sealed record Note(string Title, string Body);

    private sealed record Device(SyncEngine Engine, SyncedCollection<Note> Notes, SyncedCollection<Note> Secrets);

    private Device NewDevice(string name, ISyncTransport? transport = null)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db"), TestKeys.Local));
        var engine = Own(NewEngine(db, transport ?? _server.Connect()));
        var notes = Own(new SyncedCollection<Note>(engine, new() { Name = "notes.items", ConflictPolicy = SyncConflictPolicy.KeepBoth }));
        var secrets = Own(new SyncedCollection<Note>(engine, new() { Name = "vault.items", GuardDeletions = true }));
        return new Device(engine, notes, secrets);
    }

    private SyncEngine NewEngine(SyncDatabase db, ISyncTransport transport) =>
        new(db, transport, new InMemoryMasterKeyStore(_key), [], time: _time, debounce: TimeSpan.FromHours(1));

    private static async Task SyncAll(params Device[] devices)
    {
        foreach (var device in devices)
        {
            var result = await device.Engine.SyncNowAsync();
            Assert.Equal(SyncRunOutcome.Completed, result.Outcome);
        }
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }
}
