using System.Security.Cryptography;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Tests;

public sealed class VaultBackupTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> _owned = [];

    private string BackupFolder => Path.Combine(_dir, "My Drive", "Backups");

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task A_lost_vault_comes_back_whole_on_a_new_device_from_the_backup_alone()
    {
        var a = await NewVault("pc");
        var login = a.Store.Add(Login("Bank", "hunter2"));
        var doc = a.Store.Add(VaultItem.New(VaultItemKind.Document, "Passport"));
        var data = RandomNumberGenerator.GetBytes(BlobStore.ChunkSize + 99);
        await a.Files.AttachAsync(doc, "passport.pdf", "application/pdf", new MemoryStream(data));
        var trashed = a.Store.Add(Login("Old", "x"));
        a.Store.MoveToTrash(trashed);
        var result = await a.Backup.BackUpAsync();
        Assert.True(result.Written);
        Assert.Equal(2, result.ChunksCopied);

        // A new phone, nothing synced: only the backup folder and the password.
        var b = NewDevice("phone");
        var repository = Assert.Single(await VaultBackupRepository.FindAsync(new FolderBackupTarget(BackupFolder)));
        using var key = VaultKeyring.UnlockWithPassword(repository.Keyring, Password);
        Assert.Equal(3, await b.Backup.RestoreVaultAsync(repository, repository.Snapshots[0], key));

        Assert.Equal(VaultState.Unlocked, b.Session.State);
        Assert.Equal(a.Session.VaultId, b.Session.VaultId);
        Assert.Equal("hunter2", PasswordOf(b.Store.Get(login)!.Item));
        Assert.Equal(data, await b.Files.ReadAllAsync(Assert.Single(b.Store.Get(doc)!.Item.Attachments)));
        Assert.Single(b.Store.Trash());

        b.Session.Lock("test");
        await b.Session.UnlockAsync(Password);
    }

    [Fact]
    public async Task The_recovery_key_opens_a_backup_too()
    {
        var created = await NewVault("pc", returnRecovery: true);
        created.Store.Add(Login("Bank", "x"));
        await created.Backup.BackUpAsync();
        var repository = Assert.Single(await VaultBackupRepository.FindAsync(new FolderBackupTarget(BackupFolder)));
        using var key = VaultKeyring.UnlockWithRecoveryKey(repository.Keyring, created.RecoveryKey!);
        var snapshot = await repository.ReadSnapshotAsync(repository.Snapshots[0], key);
        Assert.Single(snapshot.Records);
    }

    [Fact]
    public async Task Nothing_in_the_backup_folder_is_readable_without_the_key()
    {
        var a = await NewVault("pc");
        a.Store.Add(Login("Bank-marker", "hunter2-marker"));
        await a.Files.AttachAsync(a.Store.Add(VaultItem.New(VaultItemKind.Document, "Doc")), "id.txt", "text/plain",
            new MemoryStream("PASSPORT-B1234567"u8.ToArray()));
        await a.Backup.BackUpAsync();

        foreach (var file in Directory.EnumerateFiles(BackupFolder, "*", SearchOption.AllDirectories))
        {
            var text = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(file));
            Assert.DoesNotContain("hunter2", text);
            Assert.DoesNotContain("Bank-marker", text);
            Assert.DoesNotContain("B1234567", text);
        }
    }

    [Fact]
    public async Task An_unchanged_vault_adds_no_snapshot_but_is_verified_again()
    {
        var a = await NewVault("pc");
        a.Store.Add(Login("Bank", "x"));
        var first = await a.Backup.BackUpAsync();
        _clock.Advance(TimeSpan.FromDays(1));
        await a.Session.UnlockAsync(Password);
        var second = await a.Backup.BackUpAsync();

        Assert.False(second.Written);
        Assert.Equal(first.Snapshot, second.Snapshot);
        Assert.Equal(_clock.GetUtcNow(), a.Backup.LastGoodBackup);
    }

    [Fact]
    public async Task A_missing_chunk_is_copied_again_and_a_damaged_snapshot_is_replaced()
    {
        var a = await NewVault("pc");
        await a.Files.AttachAsync(a.Store.Add(VaultItem.New(VaultItemKind.Document, "Doc")), "d.bin", "application/octet-stream",
            new MemoryStream(RandomNumberGenerator.GetBytes(1000)));
        var first = await a.Backup.BackUpAsync();
        var repoDir = Path.Combine(BackupFolder, VaultBackupRepository.DirectoryFor(a.Session.VaultId!));
        File.Delete(Directory.EnumerateFiles(Path.Combine(repoDir, "blobs"), "*.chunk", SearchOption.AllDirectories).Single());
        var snapshotFile = Path.Combine(repoDir, "snapshots", first.Snapshot);
        var bytes = await File.ReadAllBytesAsync(snapshotFile);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(snapshotFile, bytes);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await a.Backup.BackUpAsync();

        Assert.True(second.Written);
        Assert.Equal(1, second.ChunksCopied);
    }

    [Fact]
    public async Task Tampered_or_renamed_snapshots_are_refused()
    {
        var a = await NewVault("pc");
        a.Store.Add(Login("One", "x"));
        var old = await a.Backup.BackUpAsync();
        var repository = Assert.Single(await VaultBackupRepository.FindAsync(new FolderBackupTarget(BackupFolder)));
        var snapshots = Path.Combine(BackupFolder, repository.Directory, "snapshots");
        var oldBytes = await File.ReadAllBytesAsync(Path.Combine(snapshots, old.Snapshot));
        _clock.Advance(TimeSpan.FromMinutes(1));
        a.Store.Add(Login("Two", "x"));
        var latest = await a.Backup.BackUpAsync();

        // Passing the old snapshot off as a newer one (a storage provider rolling the vault back).
        var fake = VaultBackupRepository.SnapshotName(_clock.GetUtcNow().AddDays(1));
        await File.WriteAllBytesAsync(Path.Combine(snapshots, fake), oldBytes);
        using var key = VaultKeyring.UnlockWithPassword(repository.Keyring, Password);
        await Assert.ThrowsAsync<VaultKeyException>(() => repository.ReadSnapshotAsync(fake, key));
        Assert.Equal(2, (await repository.ReadSnapshotAsync(latest.Snapshot, key)).Records.Count);
        using var other = VaultKey.Create();
        await Assert.ThrowsAsync<VaultKeyException>(() => repository.ReadSnapshotAsync(latest.Snapshot, other));
    }

    [Fact]
    public async Task Items_deleted_for_good_come_back_from_the_backup_with_their_documents()
    {
        var a = await NewVault("pc");
        var keep = a.Store.Add(Login("Keep", "x"));
        var lost = a.Store.Add(VaultItem.New(VaultItemKind.Document, "Lost"));
        var data = RandomNumberGenerator.GetBytes(5000);
        await a.Files.AttachAsync(lost, "l.pdf", "application/pdf", new MemoryStream(data));
        await a.Backup.BackUpAsync();
        a.Store.Save(keep, Login("Keep", "edited after the backup"));
        a.Store.MoveToTrash(lost);
        a.Store.Purge(lost);

        var repository = Assert.Single(await VaultBackupRepository.FindAsync(new FolderBackupTarget(BackupFolder)));
        using var key = VaultKeyring.UnlockWithPassword(repository.Keyring, Password);
        Assert.Equal(1, await a.Backup.RestoreMissingAsync(repository, repository.Snapshots[0], key));

        Assert.Equal("edited after the backup", PasswordOf(a.Store.Get(keep)!.Item));
        Assert.Equal(data, await a.Files.ReadAllAsync(Assert.Single(a.Store.Get(lost)!.Item.Attachments)));
    }

    [Fact]
    public async Task A_whole_vault_restore_is_refused_where_a_vault_exists()
    {
        var a = await NewVault("pc");
        a.Store.Add(Login("x", "x"));
        await a.Backup.BackUpAsync();
        var repository = Assert.Single(await VaultBackupRepository.FindAsync(new FolderBackupTarget(BackupFolder)));
        using var key = VaultKeyring.UnlockWithPassword(repository.Keyring, Password);
        await Assert.ThrowsAsync<VaultKeyException>(() => a.Backup.RestoreVaultAsync(repository, repository.Snapshots[0], key));
    }

    [Fact]
    public async Task Backups_need_an_unlocked_vault_and_a_reachable_folder()
    {
        var a = await NewVault("pc");
        a.Session.Lock("test");
        await Assert.ThrowsAsync<VaultLockedException>(() => a.Backup.BackUpAsync());
        await a.Session.UnlockAsync(Password);
        a.Settings.Get<VaultSettings>(VaultSettings.StoreId).Update(s => s.BackupLocation = Path.Combine(_dir, "unplugged-usb"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Backup.BackUpAsync());
        Assert.NotNull(a.Backup.LastError);
        Assert.True(a.Backup.IsOverdue);
    }

    [Theory]
    [InlineData(".chunk")]
    [InlineData(".hvs")]
    public async Task A_backup_that_does_not_read_back_is_not_counted_as_good(string corrupted)
    {
        var device = NewDevice("pc", target => new LyingTarget(target, corrupted));
        await device.Session.CreateAsync(Password);
        await device.Files.AttachAsync(device.Store.Add(VaultItem.New(VaultItemKind.Document, "Doc")), "d.bin", "application/octet-stream",
            new MemoryStream(RandomNumberGenerator.GetBytes(1000)));

        await Assert.ThrowsAnyAsync<Exception>(() => device.Backup.BackUpAsync());
        Assert.Null(device.Backup.LastGoodBackup);
        Assert.NotNull(device.Backup.LastError);
    }

    /// <summary>Storage that accepts writes but keeps garbage for some files (a failing disk or a buggy sync client).</summary>
    private sealed class LyingTarget(IBackupTarget inner, string corruptedExtension) : IBackupTarget
    {
        public string DisplayName => inner.DisplayName;
        public Task<bool> ExistsAsync(string path, CancellationToken ct) => inner.ExistsAsync(path, ct);
        public Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken ct) => inner.ListFilesAsync(directory, ct);
        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string directory, CancellationToken ct) => inner.ListDirectoriesAsync(directory, ct);
        public Task<byte[]?> ReadAsync(string path, CancellationToken ct) => inner.ReadAsync(path, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);
        public Task DeleteDirectoryAsync(string directory, CancellationToken ct) => inner.DeleteDirectoryAsync(directory, ct);

        public Task WriteAsync(string path, byte[] data, CancellationToken ct)
        {
            if (!path.EndsWith(corruptedExtension, StringComparison.Ordinal)) return inner.WriteAsync(path, data, ct);
            var garbage = data.ToArray();
            garbage[^1] ^= 0xFF;
            return inner.WriteAsync(path, garbage, ct);
        }
    }

    private sealed class WrappedLocation(Func<IBackupTarget, IBackupTarget> wrap) : IVaultBackupLocation
    {
        public IBackupTarget? Open(string? location) => new FolderBackupLocation().Open(location) is { } target ? wrap(target) : null;
    }

    [Fact]
    public void Retention_keeps_the_newest_of_each_day_and_month()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var names = Enumerable.Range(0, 120).SelectMany(d => new[] { start.AddDays(d), start.AddDays(d).AddHours(5) })
            .Select(VaultBackupRepository.SnapshotName).ToList();

        var keep = VaultBackupService.SnapshotsToKeep(names, keepDaily: 30, keepMonthly: 12);

        var days = keep.Select(n => VaultBackupRepository.SnapshotTime(n)!.Value).ToList();
        Assert.Contains(VaultBackupRepository.SnapshotName(start.AddDays(119).AddHours(5)), keep);
        Assert.Equal(30 + 3, keep.Count); // 30 latest days (all in April), plus the newest of January, February, March
        Assert.All(days, d => Assert.Equal(14, d.Hour));
    }

    private static VaultItem Login(string title, string password) => VaultItem.New(VaultItemKind.Login, title) with
    {
        Fields = [new VaultField("Password", password, VaultFieldKind.Password)],
    };

    private static string PasswordOf(VaultItem item) => item.Fields.Single(f => f.Kind == VaultFieldKind.Password).Value;

    private sealed record Device(VaultSession Session, VaultStore Store, VaultFiles Files, VaultBackupService Backup, ISettingsStoreFactory Settings)
    {
        public string? RecoveryKey { get; init; }
    }

    private async Task<Device> NewVault(string name, bool returnRecovery = false)
    {
        var device = NewDevice(name);
        var recovery = await device.Session.CreateAsync(Password);
        return returnRecovery ? device with { RecoveryKey = recovery } : device;
    }

    /// <summary>A device with sync switched off: everything, including documents, lives only on it and in the backup.</summary>
    private Device NewDevice(string name, Func<IBackupTarget, IBackupTarget>? wrapTarget = null)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory, _clock);
        var engine = Own(new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), [], time: _clock, debounce: TimeSpan.FromHours(1), blobs: blobs));
        var records = Own(new SyncedCollection<VaultItemRecord>(engine, VaultStore.Options));
        var keyrings = Own(new SyncedCollection<VaultKeyringData>(engine, new SyncedCollectionOptions<VaultKeyringData> { Name = VaultSession.KeyringCollection }));
        var settings = Own(new SettingsStoreFactory(paths));
        Directory.CreateDirectory(BackupFolder);
        settings.Get<VaultSettings>(VaultSettings.StoreId).Update(s => s.BackupLocation = BackupFolder);
        var session = Own(new VaultSession(keyrings, settings, new NoDeviceUnlock(), engine, _clock) { NewKdf = VaultCryptoTests.CheapKdf });
        var store = Own(new VaultStore(records, session, _clock));
        var backup = new VaultBackupService(session, store, blobs,
            wrapTarget is null ? new FolderBackupLocation() : new WrappedLocation(wrapTarget), settings, time: _clock);
        return new Device(session, store, new VaultFiles(store, blobs, session), backup, settings);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }
}
