using System.Security.Cryptography;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Tests;

public sealed class BlobStoreTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeSyncServer _server = new();
    private readonly byte[] _syncKey = SyncKeyring.CreateMasterKey();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> _owned = [];

    public BlobStoreTests() => _server.Now = () => _clock.GetUtcNow();

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64 * 1024 - 1)]
    [InlineData(BlobStore.ChunkSize)]
    [InlineData(BlobStore.ChunkSize + 1)]
    [InlineData(2 * BlobStore.ChunkSize + 12345)]
    public async Task Files_of_any_size_round_trip_locally(int size)
    {
        var store = NewStore("a", new NullBlobTransport());
        var data = RandomNumberGenerator.GetBytes(size);

        var blob = await store.ImportAsync(new MemoryStream(data));

        Assert.Equal(size, blob.Size);
        Assert.Equal(Math.Max(1, (size + BlobStore.ChunkSize - 1) / BlobStore.ChunkSize), blob.ChunkCount);
        Assert.Equal(data, await store.ReadAllAsync(blob));
        Assert.True(store.IsPending(blob.Id));
    }

    [Fact]
    public async Task Chunks_on_disk_are_ciphertext_and_padded()
    {
        var store = NewStore("a", new NullBlobTransport());
        var marker = System.Text.Encoding.UTF8.GetBytes("PASSPORT-NUMBER-B1234567");
        var blob = await store.ImportAsync(new MemoryStream(marker));

        var chunk = await File.ReadAllBytesAsync(Path.Combine(_dir, "a", "sync", "blobs", blob.Id, "0.chunk"));
        Assert.Equal(64 * 1024 + 28, chunk.Length);
        Assert.Equal(-1, chunk.AsSpan().IndexOf(marker));
    }

    [Fact]
    public async Task Tampered_swapped_or_foreign_chunks_are_refused()
    {
        var store = NewStore("a", new NullBlobTransport());
        var one = await store.ImportAsync(new MemoryStream(RandomNumberGenerator.GetBytes(2 * BlobStore.ChunkSize)));
        var other = await store.ImportAsync(new MemoryStream(RandomNumberGenerator.GetBytes(10)));
        string Chunk(BlobRef b, int i) => Path.Combine(_dir, "a", "sync", "blobs", b.Id, $"{i}.chunk");

        var c0 = await File.ReadAllBytesAsync(Chunk(one, 0));
        var c1 = await File.ReadAllBytesAsync(Chunk(one, 1));
        await File.WriteAllBytesAsync(Chunk(one, 0), c1);
        await File.WriteAllBytesAsync(Chunk(one, 1), c0);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.ReadAllAsync(one));

        await File.WriteAllBytesAsync(Chunk(one, 0), await File.ReadAllBytesAsync(Chunk(other, 0)));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.ReadAllAsync(one));

        c0[40] ^= 1;
        await File.WriteAllBytesAsync(Chunk(one, 0), c0);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.ReadAllAsync(one));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.ReadAllAsync(one with { Key = new byte[32] }));
    }

    [Fact]
    public async Task A_record_that_uses_a_file_waits_until_the_file_is_on_the_server()
    {
        var transport = _server.Connect();
        var a = NewDevice("a", transport);
        await a.Session.CreateAsync(Password);
        var uid = a.Store.Add(VaultItem.New(VaultItemKind.Document, "Passport"));
        await Sync(a);

        transport.FailChunkUploadsAfter = 1;
        await a.Files.AttachAsync(uid, "passport.pdf", "application/pdf", new MemoryStream(RandomNumberGenerator.GetBytes(3 * BlobStore.ChunkSize)));
        Assert.Equal(SyncRunOutcome.Offline, (await a.Engine.SyncNowAsync()).Outcome);

        // The server has one chunk, no commit, and still the item without the attachment.
        var blob = Assert.Single(_server.Blobs.Values);
        Assert.False(blob.Committed);
        Assert.Single(blob.Chunks);
        var b = NewDevice("b");
        await Sync(b);
        await b.Session.UnlockAsync(Password);
        Assert.Empty(b.Store.Get(uid)!.Item.Attachments);

        // Resumed: only the missing chunks are sent, then the item.
        transport.FailChunkUploadsAfter = null;
        var before = transport.ChunkUploads;
        await Sync(a, b);
        Assert.Equal(2, transport.ChunkUploads - before);
        Assert.True(_server.Blobs.Values.Single().Committed);
        Assert.Single(b.Store.Get(uid)!.Item.Attachments);
    }

    [Fact]
    public async Task Another_device_opens_the_document()
    {
        var a = NewDevice("a");
        var b = NewDevice("b");
        await a.Session.CreateAsync(Password);
        var uid = a.Store.Add(VaultItem.New(VaultItemKind.Document, "ID card"));
        var data = RandomNumberGenerator.GetBytes(BlobStore.ChunkSize + 777);
        await a.Files.AttachAsync(uid, "id.jpg", "image/jpeg", new MemoryStream(data));
        await Sync(a, b);
        await b.Session.UnlockAsync(Password);

        var attachment = Assert.Single(b.Store.Get(uid)!.Item.Attachments);
        Assert.False(b.Files.IsOnThisDevice(attachment));
        Assert.Equal(data, await b.Files.ReadAllAsync(attachment));
        Assert.True(b.Files.IsOnThisDevice(attachment));
        Assert.False(a.Files.IsUploading(attachment));
    }

    [Fact]
    public async Task A_full_quota_holds_back_only_the_records_that_need_the_file()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        var withFile = a.Store.Add(VaultItem.New(VaultItemKind.Document, "Scan"));
        await Sync(a);
        _server.BlobQuota = 1000;

        await a.Files.AttachAsync(withFile, "scan.pdf", "application/pdf", new MemoryStream(RandomNumberGenerator.GetBytes(5000)));
        var other = a.Store.Add(VaultItem.New(VaultItemKind.Note, "Wifi"));
        Assert.Equal(SyncRunOutcome.QuotaExceeded, (await a.Engine.SyncNowAsync()).Outcome);
        Assert.Equal(SyncState.QuotaExceeded, a.Engine.Status.State);

        var b = NewDevice("b");
        await Sync(b);
        await b.Session.UnlockAsync(Password);
        Assert.NotNull(b.Store.Get(other));
        Assert.Empty(b.Store.Get(withFile)!.Item.Attachments);
    }

    [Fact]
    public async Task Removing_an_attachment_keeps_it_in_the_history_and_on_the_server()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        var uid = a.Store.Add(VaultItem.New(VaultItemKind.Document, "Contract"));
        var attachment = await a.Files.AttachAsync(uid, "c.pdf", "application/pdf", new MemoryStream(RandomNumberGenerator.GetBytes(100)));
        await Sync(a);
        a.Files.Remove(uid, attachment.Id);
        _clock.Advance(TimeSpan.FromDays(10));
        await a.Session.UnlockAsync(Password);
        await Sync(a);

        var record = Assert.Single(a.Records.All()).Value;
        Assert.Contains(attachment.Blob.Id, record.Blobs);
        Assert.Null(_server.Blobs[attachment.Blob.Id].DeletedAt);
    }

    [Fact]
    public async Task Unused_blobs_are_deleted_after_the_grace_period_and_used_ones_restored()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        var uid = a.Store.Add(VaultItem.New(VaultItemKind.Document, "Old scan"));
        var attachment = await a.Files.AttachAsync(uid, "old.pdf", "application/pdf", new MemoryStream(RandomNumberGenerator.GetBytes(100)));
        await Sync(a);

        // Gone from the item and from its history: trash and purge.
        a.Store.MoveToTrash(uid);
        a.Store.Purge(uid);
        await Sync(a);
        Assert.Null(_server.Blobs[attachment.Blob.Id].DeletedAt);

        _clock.Advance(TimeSpan.FromDays(8));
        await Sync(a);
        Assert.NotNull(_server.Blobs[attachment.Blob.Id].DeletedAt);
        Assert.False(Directory.Exists(Path.Combine(_dir, "a", "sync", "blobs", attachment.Blob.Id)));

        // A record that uses a deleted blob (e.g. restored from a backup) brings it back.
        await a.Session.UnlockAsync(Password);
        a.Records.Upsert("restored", VaultItemSealer.Seal(a.Session.Key, a.Session.VaultId!, 1, "restored",
            VaultItem.New(VaultItemKind.Document, "Back") with { Attachments = [attachment] }, false, null));
        _clock.Advance(TimeSpan.FromDays(2));
        await Sync(a);
        Assert.Null(_server.Blobs[attachment.Blob.Id].DeletedAt);
    }

    [Fact]
    public async Task Blob_clean_up_never_runs_beside_records_this_build_cannot_read()
    {
        var a = NewDevice("a");
        await a.Session.CreateAsync(Password);
        var uid = a.Store.Add(VaultItem.New(VaultItemKind.Document, "Scan"));
        var attachment = await a.Files.AttachAsync(uid, "s.pdf", "application/pdf", new MemoryStream(RandomNumberGenerator.GetBytes(100)));
        await Sync(a);

        // A device without the vault module: it syncs the vault records but knows nothing about their files.
        var paths = new HelmPaths(Path.Combine(_dir, "plain"));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var transport = _server.Connect();
        var blobs = new BlobStore(db, transport, paths.SyncBlobsDirectory, _clock);
        var engine = Own(new SyncEngine(db, transport, new InMemoryMasterKeyStore(_syncKey), [], time: _clock, debounce: TimeSpan.FromHours(1), blobs: blobs));
        _clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(SyncRunOutcome.Completed, (await engine.SyncNowAsync()).Outcome);

        Assert.Null(_server.Blobs[attachment.Blob.Id].DeletedAt);
    }

    private BlobStore NewStore(string name, IBlobTransport transport)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        return new BlobStore(db, transport, paths.SyncBlobsDirectory, _clock);
    }

    private sealed record Device(SyncEngine Engine, VaultSession Session, VaultStore Store, VaultFiles Files, SyncedCollection<VaultItemRecord> Records);

    private Device NewDevice(string name, FakeSyncServer.Transport? transport = null)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        transport ??= _server.Connect();
        var blobs = new BlobStore(db, transport, paths.SyncBlobsDirectory, _clock);
        var engine = Own(new SyncEngine(db, transport, new InMemoryMasterKeyStore(_syncKey), [], time: _clock,
            debounce: TimeSpan.FromHours(1), blobs: blobs));
        var records = Own(new SyncedCollection<VaultItemRecord>(engine, VaultStore.Options));
        var keyrings = Own(new SyncedCollection<VaultKeyringData>(engine, new SyncedCollectionOptions<VaultKeyringData> { Name = VaultSession.KeyringCollection }));
        var settings = Own(new SettingsStoreFactory(paths));
        var session = Own(new VaultSession(keyrings, settings, new NoDeviceUnlock(), engine, _clock) { NewKdf = VaultCryptoTests.CheapKdf });
        var store = Own(new VaultStore(records, session, _clock));
        return new Device(engine, session, store, new VaultFiles(store, blobs, session), records);
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
}
