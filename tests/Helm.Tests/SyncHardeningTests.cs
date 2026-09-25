using System.Security.Cryptography;
using System.Text;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Microsoft.Data.Sqlite;

namespace Helm.Tests;

/// <summary>Local encryption at rest, key epochs and re-encryption after a rotation.</summary>
public sealed class SyncHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();
    private readonly byte[] _k1 = SyncKeyring.CreateMasterKey();
    private readonly byte[] _k2 = SyncKeyring.CreateMasterKey();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Record_bodies_are_encrypted_on_disk()
    {
        var path = Path.Combine(_dir, "a.db");
        using (var db = new SyncDatabase(path, TestKeys.Local))
        using (var engine = new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), []))
        using (var notes = new SyncedCollection<Note>(engine, new() { Name = "notes" }))
        {
            notes.Upsert("n1", new Note("Bank", "PIN is 4242 and the secret word is marmalade"));
            Assert.Equal("marmalade", notes.Get("n1")!.Body[^9..]);
        }

        var onDisk = string.Concat(Directory.GetFiles(_dir).Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))));
        Assert.DoesNotContain("marmalade", onDisk);
        Assert.DoesNotContain("4242", onDisk);
    }

    [Fact]
    public void Opening_a_replica_with_another_local_key_fails_fast()
    {
        var path = Path.Combine(_dir, "a.db");
        new SyncDatabase(path, TestKeys.Local).Dispose();

        Assert.Throws<SyncLocalKeyException>(() => new SyncDatabase(path, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void A_plaintext_replica_from_helm_0_5_is_encrypted_in_place()
    {
        var path = Path.Combine(_dir, "old.db");
        CreateVersion1Replica(path, "notes", "n1", """{"title":"Old","body":"written by 0.5 in the clear"}""");

        using (var db = new SyncDatabase(path, TestKeys.Local))
        using (var engine = new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), []))
        using (var notes = new SyncedCollection<Note>(engine, new() { Name = "notes" }))
        {
            Assert.Equal(new Note("Old", "written by 0.5 in the clear"), notes.Get("n1"));
        }
        SqliteConnection.ClearAllPools();
        var onDisk = string.Concat(Directory.GetFiles(_dir).Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))));
        Assert.DoesNotContain("in the clear", onDisk);
    }

    [Fact]
    public void A_lost_local_key_moves_the_unreadable_replica_aside_and_starts_fresh()
    {
        var paths = new HelmPaths(_dir);
        using (var db = SyncServices.OpenDatabase(paths, null))
        using (var engine = new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), []))
        using (var notes = new SyncedCollection<Note>(engine, new() { Name = "notes" }))
            notes.Upsert("n1", new Note("t", "b"));

        File.Delete(paths.SyncLocalKeyFile);
        using (var db = SyncServices.OpenDatabase(paths, null))
        using (var engine = new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), []))
        using (var notes = new SyncedCollection<Note>(engine, new() { Name = "notes" }))
            Assert.Empty(notes.All());

        Assert.Single(Directory.GetFiles(paths.SyncDirectory, "helm-sync.db.unreadable-*"), f => !f.EndsWith("-wal") && !f.EndsWith("-shm"));
    }

    [Fact]
    public async Task Data_sealed_with_a_newer_key_epoch_stops_the_run_and_nothing_is_skipped()
    {
        var a = NewDevice("a", SyncKeySet.Single(_k1));
        var b = NewDevice("b", SyncKeySet.Single(_k1));
        var id = a.Notes.Add(new Note("Plan", "v1"));
        await Sync(a, b);
        var keyChanged = 0;
        b.Engine.KeyEpochChanged += (_, _) => keyChanged++;

        // A rotates: epoch 2, then re-encrypts everything and writes a new note.
        Rotate(a);
        await Sync(a);
        var fresh = a.Notes.Add(new Note("After rotation", "epoch 2"));
        await Sync(a);

        var blocked = await b.Engine.SyncNowAsync();
        Assert.Equal(SyncRunOutcome.KeyChanged, blocked.Outcome);
        Assert.Equal(SyncState.KeyChanged, b.Engine.Status.State);
        Assert.Equal(1, keyChanged);
        Assert.Equal(new Note("Plan", "v1"), b.Notes.Get(id)); // local copy untouched
        Assert.Null(b.Notes.Get(fresh));

        // B unlocks again and gets both epochs: the run resumes from where it stopped.
        b.Keys.Save(TwoEpochs().Serialize());
        b.Engine.ReloadKey();
        Assert.Equal(SyncRunOutcome.Completed, (await b.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(new Note("After rotation", "epoch 2"), b.Notes.Get(fresh));
        Assert.Equal(new Note("Plan", "v1"), b.Notes.Get(id));
    }

    [Fact]
    public async Task After_a_rotation_the_server_holds_only_new_epoch_payloads()
    {
        var a = NewDevice("a", SyncKeySet.Single(_k1));
        for (var i = 0; i < 5; i++) a.Notes.Upsert($"n{i}", new Note($"#{i}", "body"));
        a.Notes.Delete("n4");
        await Sync(a);
        Assert.All(_server.Records, r => Assert.Equal(1, EpochOf(r.Payload)));

        Rotate(a);
        await Sync(a);

        Assert.All(_server.Records, r => Assert.Equal(2, EpochOf(r.Payload)));
        Assert.Equal(4, a.Notes.All().Count);
    }

    [Fact]
    public async Task Re_encryption_never_creates_conflict_copies_and_loses_to_real_edits()
    {
        var a = NewDevice("a", SyncKeySet.Single(_k1));
        var b = NewDevice("b", SyncKeySet.Single(_k1));
        var id = a.Notes.Add(new Note("Plan", "v1"));
        await Sync(a, b);

        // B edits (still on epoch 1) before A's re-encryption reaches the server.
        b.Notes.Upsert(id, new Note("Plan", "edited on B"));
        await Sync(b);
        Rotate(a);
        await Sync(a);

        Assert.Equal(new Note("Plan", "edited on B"), a.Notes.Get(id));
        Assert.Single(a.Notes.All());
    }

    [Fact]
    public async Task Payloads_in_the_helm_0_5_envelope_format_are_still_read()
    {
        var reader = NewDevice("reader", SyncKeySet.Single(_k1));
        var body = """{"title":"From 0.5","body":"v1 envelope"}""";
        var envelope = Version1Envelope(_k1, "notes", "legacy", new SealedPlain(1, 1_800_000_000_000, "OLDDEVICE", body));
        await _server.Connect().PushAsync([new PushItem("notes", "legacy", 0, false, envelope)], CancellationToken.None);

        await Sync(reader);

        Assert.Equal(new Note("From 0.5", "v1 envelope"), reader.Notes.Get("legacy"));
    }

    private sealed record Note(string Title, string Body);

    private sealed record Device(SyncEngine Engine, SyncedCollection<Note> Notes, InMemoryMasterKeyStore Keys, SyncDatabase Db);

    private Device NewDevice(string name, SyncKeySet keys)
    {
        var db = Own(new SyncDatabase(Path.Combine(_dir, name + ".db"), TestKeys.Local));
        var store = new InMemoryMasterKeyStore(keys.Serialize());
        var engine = Own(new SyncEngine(db, _server.Connect(), store, [], debounce: TimeSpan.FromHours(1)));
        var notes = Own(new SyncedCollection<Note>(engine, new()
        {
            Name = "notes",
            ConflictPolicy = SyncConflictPolicy.KeepBoth,
            CreateConflictCopy = n => n with { Title = n.Title + " (conflict)" },
        }));
        return new Device(engine, notes, store, db);
    }

    private SyncKeySet TwoEpochs() => new(2, new Dictionary<int, byte[]> { [1] = _k1, [2] = _k2 });

    /// <summary>What <see cref="SyncSetupService.RemoveDeviceAndRotateKeyAsync"/> does locally after uploading the keyring.</summary>
    private void Rotate(Device device)
    {
        device.Keys.Save(TwoEpochs().Serialize());
        device.Engine.ReloadKey();
        device.Db.MarkAllForReseal();
    }

    private static async Task Sync(params Device[] devices)
    {
        foreach (var device in devices) Assert.Equal(SyncRunOutcome.Completed, (await device.Engine.SyncNowAsync()).Outcome);
    }

    private static int EpochOf(byte[] payload) => payload[0] == 2 ? (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(1, 4)) : 1;

    private sealed record SealedPlain(int SchemaVersion, long UpdatedAtMs, string DeviceId, string Body);

    /// <summary>A payload exactly as Helm 0.5.0 sealed it: [0x01][nonce][tag][ciphertext], AAD "helm-sync/v1|c|id".</summary>
    private static byte[] Version1Envelope(byte[] master, string collection, string id, SealedPlain content)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: [], info: Encoding.UTF8.GetBytes("helm-sync/v1/collection:" + collection));
        var plaintext = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        {
            s = content.SchemaVersion, t = content.UpdatedAtMs, d = content.DeviceId, b = content.Body,
        }));
        var envelope = new byte[1 + 12 + 16 + plaintext.Length];
        envelope[0] = 1;
        RandomNumberGenerator.Fill(envelope.AsSpan(1, 12));
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(envelope.AsSpan(1, 12), plaintext, envelope.AsSpan(29), envelope.AsSpan(13, 16), Encoding.UTF8.GetBytes($"helm-sync/v1|{collection}|{id}"));
        return envelope;
    }

    /// <summary>The replica schema of Helm 0.5.0: plaintext bodies, user_version 1.</summary>
    private static void CreateVersion1Replica(string path, string collection, string id, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE records (collection TEXT NOT NULL, id TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 0,
                schema_version INTEGER NOT NULL, deleted INTEGER NOT NULL DEFAULT 0, updated_at INTEGER NOT NULL,
                device_id TEXT NOT NULL, body TEXT, dirty INTEGER NOT NULL DEFAULT 0, local_rev INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (collection, id)) WITHOUT ROWID;
            CREATE TABLE cursors (name TEXT PRIMARY KEY, last_seq INTEGER NOT NULL);
            INSERT INTO meta (key, value) VALUES ('device_id', 'OLDDEVICE');
            INSERT INTO records (collection, id, version, schema_version, updated_at, device_id, body) VALUES ($c, $id, 3, 1, 1, 'OLDDEVICE', $b);
            PRAGMA user_version = 1;
            """;
        cmd.Parameters.AddWithValue("$c", collection);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.ExecuteNonQuery();
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }
}
