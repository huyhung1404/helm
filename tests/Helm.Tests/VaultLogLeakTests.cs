using System.Collections.Concurrent;
using System.Security.Cryptography;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Microsoft.Extensions.Logging;

namespace Helm.Tests;

/// <summary>
/// An attacker who gets Helm's log files (shared from General → Logs, or read from disk) must learn nothing secret:
/// the whole vault lifecycle runs with every logger captured, and no secret may appear in any message or exception.
/// </summary>
public sealed class VaultLogLeakTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a brand new vault password 7";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly CapturingLoggerFactory _logs = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task No_secret_reaches_the_logs_during_the_whole_vault_lifecycle()
    {
        var secrets = new List<string> { Password, NewPassword, "hunter2-LEAK-MARKER", "Bank-Title-LEAK", "4111-2222-LEAK", "Note-LEAK-body", "passport-LEAK.pdf" };
        var backupFolder = Path.Combine(_dir, "backup");
        Directory.CreateDirectory(backupFolder);
        var (session, store, files, backup) = NewDevice("pc", backupFolder);

        var recovery = await session.CreateAsync(Password);
        secrets.Add(recovery);
        var uid = store.Add(VaultItem.New(VaultItemKind.Login, "Bank-Title-LEAK") with
        {
            Fields = [new VaultField("Password", "hunter2-LEAK-MARKER", VaultFieldKind.Password), new VaultField("Card", "4111-2222-LEAK", VaultFieldKind.Secret)],
            Notes = "Note-LEAK-body",
        });
        await files.AttachAsync(uid, "passport-LEAK.pdf", "application/pdf", new MemoryStream(RandomNumberGenerator.GetBytes(2000)));
        store.Save(uid, store.Get(uid)!.Item with { Notes = "Note-LEAK-body edited" });
        await Assert.ThrowsAsync<VaultKeyException>(() => session.ChangePasswordAsync("wrong-LEAK-guess-password", NewPassword));
        secrets.Add("wrong-LEAK-guess-password");
        await session.ChangePasswordAsync(Password, NewPassword);
        secrets.Add(await session.NewRecoveryKeyAsync(NewPassword));
        await backup.BackUpAsync();
        store.MoveToTrash(uid);
        store.Purge(uid);
        var repository = Assert.Single(await VaultBackupRepository.FindAsync(new FolderBackupTarget(backupFolder)));
        using (var key = VaultKeyring.UnlockWithPassword(repository.Keyring, NewPassword))
            await backup.RestoreMissingAsync(repository, repository.Snapshots[0], key);
        await new VaultKdbxExporter(store, files).ExportAsync(new MemoryStream(), new KdbxExportOptions("kdbx-LEAK-export-password"));
        secrets.Add("kdbx-LEAK-export-password");
        session.Lock("test");
        await Assert.ThrowsAsync<VaultKeyException>(() => session.UnlockAsync("another-LEAK-wrong-password"));
        secrets.Add("another-LEAK-wrong-password");

        Assert.NotEmpty(_logs.Lines);
        foreach (var line in _logs.Lines)
            foreach (var secret in secrets)
                Assert.False(line.Contains(secret, StringComparison.OrdinalIgnoreCase), $"Log line leaks a secret: {line}");
    }

    private (VaultSession, VaultStore, VaultFiles, VaultBackupService) NewDevice(string name, string backupFolder)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory, logger: _logs.CreateLogger<BlobStore>());
        var engine = Own(new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), [], _logs.CreateLogger<SyncEngine>(),
            debounce: TimeSpan.FromHours(1), blobs: blobs));
        var records = Own(new SyncedCollection<VaultItemRecord>(engine, VaultStore.Options, _logs.CreateLogger("records")));
        var keyrings = Own(new SyncedCollection<VaultKeyringData>(engine, new SyncedCollectionOptions<VaultKeyringData> { Name = VaultSession.KeyringCollection }));
        var settings = Own(new SettingsStoreFactory(paths, _logs));
        settings.Get<VaultSettings>(VaultSettings.StoreId).Update(s => s.BackupLocation = backupFolder);
        var session = Own(new VaultSession(keyrings, settings, new NoDeviceUnlock(), engine, logger: _logs.CreateLogger<VaultSession>()) { NewKdf = VaultCryptoTests.CheapKdf });
        var store = Own(new VaultStore(records, session, logger: _logs.CreateLogger<VaultStore>()));
        var files = new VaultFiles(store, blobs, session);
        var backup = new VaultBackupService(session, store, blobs, new FolderBackupLocation(), settings, logger: _logs.CreateLogger<VaultBackupService>());
        return (session, store, files, backup);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    /// <summary>Keeps every formatted message, its structured values and any exception text.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public ILogger<T> CreateLogger<T>() => new Logger<T>(this);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}")) : "";
                owner.Lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} | {values} | {exception}");
            }
        }
    }
}
