using System.Text;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();
    private readonly byte[] _key = SyncKeyring.CreateMasterKey();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Local_edits_survive_reopening_without_any_server()
    {
        var path = Path.Combine(_dir, "solo.db");
        string id;
        using (var db = new SyncDatabase(path, TestKeys.Local))
        using (var engine = NewEngine(db, new NullSyncTransport()))
        using (var notes = new SyncedCollection<Note>(engine, NoteOptions()))
        {
            id = notes.Add(new Note("Groceries", "milk"));
            notes.Upsert("fixed", new Note("Pinned", "x"));
            Assert.True(notes.Delete("fixed"));
            Assert.False(notes.Delete("fixed"));
        }

        using var reopened = new SyncDatabase(path, TestKeys.Local);
        using var engine2 = NewEngine(reopened, new NullSyncTransport());
        using var notes2 = new SyncedCollection<Note>(engine2, NoteOptions());
        Assert.Equal(new Note("Groceries", "milk"), notes2.Get(id));
        Assert.Null(notes2.Get("fixed"));
        Assert.Single(notes2.All());
    }

    [Fact]
    public async Task Sync_without_server_or_key_is_a_no_op()
    {
        var a = NewDevice("a", transport: new NullSyncTransport());
        a.Notes.Add(new Note("t", "b"));
        Assert.Equal(SyncRunOutcome.NotConfigured, (await a.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(SyncState.NotConfigured, a.Engine.Status.State);

        var noKey = NewDevice("nokey", keys: new InMemoryMasterKeyStore());
        Assert.Equal(SyncRunOutcome.NotConfigured, (await noKey.Engine.SyncNowAsync()).Outcome);
    }

    [Fact]
    public async Task Two_devices_converge_on_creates_edits_and_deletes()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");

        var id = a.Notes.Add(new Note("Plan", "v1"));
        await SyncAll(a, b);
        Assert.Equal(new Note("Plan", "v1"), b.Notes.Get(id));

        b.Notes.Upsert(id, new Note("Plan", "v2"));
        await SyncAll(b, a);
        Assert.Equal(new Note("Plan", "v2"), a.Notes.Get(id));

        a.Notes.Delete(id);
        await SyncAll(a, b);
        Assert.Null(b.Notes.Get(id));
        Assert.Empty(b.Notes.All());
    }

    [Fact]
    public async Task Remote_changes_raise_changed_with_remote_origin()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        var events = new List<SyncedChangedEventArgs>();
        b.Notes.Changed += (_, e) => events.Add(e);

        var id = a.Notes.Add(new Note("t", "b"));
        await SyncAll(a, b);

        var remote = Assert.Single(events);
        Assert.Equal(SyncChangeOrigin.Remote, remote.Origin);
        Assert.Equal([id], remote.Ids);
    }

    [Fact]
    public async Task Keep_both_conflict_keeps_the_server_version_and_a_copy_of_the_local_edit()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        var id = a.Notes.Add(new Note("Plan", "base"));
        await SyncAll(a, b);

        a.Notes.Upsert(id, new Note("Plan", "edited on A"));
        b.Notes.Upsert(id, new Note("Plan", "edited on B"));
        await SyncAll(a, b, a);

        foreach (var device in new[] { a, b })
        {
            var all = device.Notes.All();
            Assert.Equal(2, all.Count);
            Assert.Equal(new Note("Plan", "edited on A"), device.Notes.Get(id));
            var copy = Assert.Single(all, n => n.Id != id);
            Assert.Equal(new Note("Plan (conflict)", "edited on B"), copy.Value);
        }
    }

    [Fact]
    public async Task Keep_both_prefers_an_edit_over_a_concurrent_delete()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        var id = a.Notes.Add(new Note("Plan", "base"));
        await SyncAll(a, b);

        a.Notes.Delete(id);
        b.Notes.Upsert(id, new Note("Plan", "still needed"));
        await SyncAll(a, b, a);

        Assert.Equal(new Note("Plan", "still needed"), a.Notes.Get(id));
        Assert.Equal(new Note("Plan", "still needed"), b.Notes.Get(id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Last_writer_wins_regardless_of_which_device_syncs_first(bool laterWriterSyncsFirst)
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        using var settingsA = new SyncedCollection<Note>(a.Engine, new() { Name = "prefs" });
        using var settingsB = new SyncedCollection<Note>(b.Engine, new() { Name = "prefs" });

        settingsA.Upsert("theme", new Note("theme", "dark"));
        _time.Advance(TimeSpan.FromSeconds(5));
        settingsB.Upsert("theme", new Note("theme", "light"));

        if (laterWriterSyncsFirst) await SyncAll(b, a, b);
        else await SyncAll(a, b, a);

        Assert.Equal("light", settingsA.Get("theme")!.Body);
        Assert.Equal("light", settingsB.Get("theme")!.Body);
    }

    [Fact]
    public async Task An_edit_made_while_its_push_is_in_flight_is_not_lost()
    {
        var transport = _server.Connect();
        var a = NewDevice("a", transport);
        var b = NewDevice("b");
        var id = a.Notes.Add(new Note("Draft", "first"));
        transport.AfterPushStored = () =>
        {
            transport.AfterPushStored = null;
            a.Notes.Upsert(id, new Note("Draft", "typed during the push"));
        };

        await a.Engine.SyncNowAsync();
        await SyncAll(a, b);

        Assert.Equal(new Note("Draft", "typed during the push"), b.Notes.Get(id));
    }

    [Fact]
    public async Task Records_from_a_newer_schema_are_hidden_kept_and_never_overwritten()
    {
        var oldDevice = NewDevice("old");
        var newDevice = NewDevice("new", schemaVersion: 2);
        newDevice.Notes.Upsert("n1", new Note("from v2", "has new fields"));
        await SyncAll(newDevice, oldDevice);

        Assert.Null(oldDevice.Notes.Get("n1"));
        Assert.Empty(oldDevice.Notes.All());
        Assert.Throws<InvalidOperationException>(() => oldDevice.Notes.Upsert("n1", new Note("old", "overwrite")));
        Assert.Throws<InvalidOperationException>(() => oldDevice.Notes.Delete("n1"));
        await SyncAll(oldDevice);

        var fresh = NewDevice("fresh", schemaVersion: 2);
        await SyncAll(fresh);
        Assert.Equal(new Note("from v2", "has new fields"), fresh.Notes.Get("n1"));
    }

    [Fact]
    public async Task A_later_edit_from_an_older_schema_never_overwrites_a_newer_schema_record()
    {
        var oldDevice = NewDevice("old");
        var newDevice = NewDevice("new");
        using var prefsOld = new SyncedCollection<Note>(oldDevice.Engine, new() { Name = "prefs", SchemaVersion = 1 });
        using var prefsNew = new SyncedCollection<Note>(newDevice.Engine, new() { Name = "prefs", SchemaVersion = 2 });
        prefsOld.Upsert("p", new Note("p", "v1"));
        await SyncAll(oldDevice, newDevice);

        prefsNew.Upsert("p", new Note("p", "written by schema 2"));
        _time.Advance(TimeSpan.FromSeconds(5));
        prefsOld.Upsert("p", new Note("p", "later edit by schema 1"));
        await SyncAll(newDevice, oldDevice, newDevice);

        Assert.Equal("written by schema 2", prefsNew.Get("p")!.Body);
        Assert.Null(prefsOld.Get("p"));
    }

    [Fact]
    public async Task A_conflict_found_while_pulling_is_pushed_in_the_same_run()
    {
        var transport = _server.Connect();
        var a = NewDevice("a", transport);
        var b = NewDevice("b");
        var shared = a.Notes.Add(new Note("Shared", "base"));
        await SyncAll(a, b);
        b.Notes.Upsert(shared, new Note("Shared", "edited on B"));
        await SyncAll(b);

        // A edits the shared note after its push phase, so the conflict only shows up while pulling.
        transport.BeforePull = () =>
        {
            transport.BeforePull = null;
            a.Notes.Upsert(shared, new Note("Shared", "edited on A mid-sync"));
        };
        await a.Engine.SyncNowAsync();
        await SyncAll(b);

        Assert.Contains(b.Notes.All(), n => n.Value == new Note("Shared (conflict)", "edited on A mid-sync"));
    }

    [Fact]
    public async Task Server_only_ever_sees_ciphertext()
    {
        var a = NewDevice("a");
        a.Notes.Upsert("secret-note", new Note("Bank", "PIN is 4242"));
        await a.Engine.SyncNowAsync();

        var stored = Assert.Single(_server.Records);
        var bytes = Encoding.UTF8.GetString(stored.Payload);
        Assert.DoesNotContain("4242", bytes);
        Assert.DoesNotContain("Bank", bytes);
    }

    [Fact]
    public async Task A_payload_moved_to_another_record_is_rejected_and_skipped()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        a.Notes.Upsert("x", new Note("X", "x"));
        a.Notes.Upsert("y", new Note("Y", "y"));
        await SyncAll(a);
        _server.SwapPayloads("notes", "x", "y");

        var result = await b.Engine.SyncNowAsync();

        Assert.Equal(SyncRunOutcome.Completed, result.Outcome);
        Assert.Empty(b.Notes.All());
    }

    [Fact]
    public async Task A_device_with_another_key_cannot_read_anything()
    {
        var a = NewDevice("a");
        a.Notes.Add(new Note("t", "b"));
        await SyncAll(a);

        var stranger = NewDevice("stranger", keys: new InMemoryMasterKeyStore(SyncKeyring.CreateMasterKey()));
        var result = await stranger.Engine.SyncNowAsync();

        Assert.Equal(SyncRunOutcome.Completed, result.Outcome);
        Assert.Empty(stranger.Notes.All());
    }

    [Fact]
    public async Task Large_first_upload_and_download_go_through_in_one_sync_each()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        for (var i = 0; i < 1200; i++) a.Notes.Upsert($"n{i:0000}", new Note($"#{i}", "body"));

        var pushed = await a.Engine.SyncNowAsync();
        var pulled = await b.Engine.SyncNowAsync();

        Assert.Equal(1200, pushed.Pushed);
        Assert.Equal(1200, pulled.Pulled);
        Assert.Equal(1200, b.Notes.All().Count);
    }

    [Fact]
    public async Task Offline_keeps_local_edits_and_sends_them_once_back_online()
    {
        var transport = _server.Connect();
        var a = NewDevice("a", transport);
        var b = NewDevice("b");
        var id = a.Notes.Add(new Note("Offline", "written on a plane"));

        transport.Offline = true;
        var offline = await a.Engine.SyncNowAsync();
        Assert.Equal(SyncRunOutcome.Offline, offline.Outcome);
        Assert.Equal(SyncState.Offline, a.Engine.Status.State);

        transport.Offline = false;
        await SyncAll(a, b);
        Assert.Equal(SyncState.Idle, a.Engine.Status.State);
        Assert.Equal(new Note("Offline", "written on a plane"), b.Notes.Get(id));
    }

    [Fact]
    public async Task Synced_settings_follow_the_last_writer_on_every_device()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        using var prefsA = new SyncedSettings<SharedPrefs>(a.Engine, "shell", debounce: TimeSpan.FromHours(1));
        using var prefsB = new SyncedSettings<SharedPrefs>(b.Engine, "shell", debounce: TimeSpan.FromHours(1));
        var changedOnB = 0;
        prefsB.Changed += (_, _) => changedOnB++;

        prefsA.Update(p => p.Theme = "dark");
        prefsA.Flush();
        await SyncAll(a, b);

        Assert.Equal("dark", prefsB.Current.Theme);
        Assert.Equal(1, changedOnB);
    }

    [Fact]
    public async Task Synced_log_merges_entries_from_every_device_in_creation_order()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        using var logA = new SyncedLog<Note>(a.Engine, "chat.messages");
        using var logB = new SyncedLog<Note>(b.Engine, "chat.messages");

        logA.Append(new Note("1", "from A"));
        _time.Advance(TimeSpan.FromSeconds(1));
        logB.Append(new Note("2", "from B"));
        _time.Advance(TimeSpan.FromSeconds(1));
        logA.Append(new Note("3", "from A again"));
        await SyncAll(a, b, a);

        Assert.Equal(["1", "2", "3"], logA.All().Select(e => e.Value.Title));
        Assert.Equal(["1", "2", "3"], logB.All().Select(e => e.Value.Title));
    }

    [Fact]
    public void Ids_are_ulids_and_names_are_validated()
    {
        var first = SyncIds.NewId(_time);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        var second = SyncIds.NewId(_time);

        Assert.Equal(26, first.Length);
        Assert.True(string.CompareOrdinal(first, second) < 0);
        Assert.True(SyncIds.IsValidCollection("chat.messages"));
        Assert.False(SyncIds.IsValidCollection("Notes"));
        Assert.False(SyncIds.IsValidCollection("../etc"));
        Assert.False(SyncIds.IsValidId("bad\nid"));
    }

    [Fact]
    public void Dpapi_key_store_round_trips_and_clears()
    {
        var store = new DpapiMasterKeyStore(Path.Combine(_dir, "master.key"));
        Assert.Null(store.Load());

        store.Save(_key);
        Assert.Equal(_key, store.Load());
        Assert.NotEqual(_key, File.ReadAllBytes(store.FilePath));

        store.Clear();
        Assert.Null(store.Load());
    }

    private sealed record Note(string Title, string Body);

    private sealed class SharedPrefs : IVersionedSettings
    {
        public static int CurrentVersion => 1;
        public int Version { get; set; }
        public string Theme { get; set; } = "system";
    }

    private sealed record Device(SyncEngine Engine, SyncedCollection<Note> Notes);

    private static SyncedCollectionOptions<Note> NoteOptions(int schemaVersion = 1) => new()
    {
        Name = "notes",
        SchemaVersion = schemaVersion,
        ConflictPolicy = SyncConflictPolicy.KeepBoth,
        CreateConflictCopy = n => n with { Title = n.Title + " (conflict)" },
    };

    private Device NewDevice(string name, ISyncTransport? transport = null, IMasterKeyStore? keys = null, int schemaVersion = 1)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db"), TestKeys.Local));
        var engine = Own(NewEngine(db, transport ?? _server.Connect(), keys ?? new InMemoryMasterKeyStore(_key)));
        var notes = Own(new SyncedCollection<Note>(engine, NoteOptions(schemaVersion)));
        return new Device(engine, notes);
    }

    private SyncEngine NewEngine(SyncDatabase db, ISyncTransport transport, IMasterKeyStore? keys = null) =>
        new(db, transport, keys ?? new InMemoryMasterKeyStore(_key), [], time: _time, debounce: TimeSpan.FromHours(1));

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
