using System.Security.Cryptography;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Helm.VaultRestore;

namespace Helm.Tests;

/// <summary>helm-vault-restore: a backup folder and the password are enough, without Helm or its server.</summary>
public sealed class VaultRestoreToolTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];
    private string BackupFolder => Path.Combine(_dir, "backup");

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Export_writes_every_item_and_document_from_the_backup()
    {
        var (recovery, data) = await MakeBackup();
        var output = Path.Combine(_dir, "restored");
        var (code, text) = await Run(["export", BackupFolder, output], Password);

        Assert.Equal(0, code);
        Assert.Contains("NOT encrypted", text);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "items.json")));
        var items = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var bank = items.Single(i => i.GetProperty("title").GetString() == "Bank");
        Assert.Contains(bank.GetProperty("fields").EnumerateArray(), f => f.GetProperty("value").GetString() == "hunter2");
        Assert.Equal("old", bank.GetProperty("history")[0].GetProperty("fields")[0].GetProperty("value").GetString());
        var file = items.Single(i => i.GetProperty("title").GetString() == "Passport").GetProperty("attachments")[0].GetProperty("file").GetString()!;
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(output, file)));

        // The recovery key works too, straight on the repository folder.
        var repo = Directory.GetDirectories(BackupFolder).Single();
        Assert.Equal(0, (await Run(["export", repo, Path.Combine(_dir, "by-recovery"), "--recovery"], recovery)).Code);
    }

    [Fact]
    public async Task List_needs_no_password_and_shows_the_snapshots()
    {
        await MakeBackup();
        var (code, text) = await Run(["list", BackupFolder], "");
        Assert.Equal(0, code);
        Assert.Contains("Vault ", text);
        Assert.Contains(".hvs", text);
    }

    [Fact]
    public async Task A_wrong_password_or_a_used_output_folder_is_refused()
    {
        await MakeBackup();
        var (code, text) = await Run(["export", BackupFolder, Path.Combine(_dir, "out")], "not the vault password");
        Assert.Equal(1, code);
        Assert.Contains("not correct", text);
        Assert.False(File.Exists(Path.Combine(_dir, "out", "items.json")));

        var used = Path.Combine(_dir, "used");
        Directory.CreateDirectory(used);
        await File.WriteAllTextAsync(Path.Combine(used, "keep.txt"), "mine");
        Assert.Equal(1, (await Run(["export", BackupFolder, used], Password)).Code);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(used, "keep.txt")));
    }

    [KeePassXcFact]
    public async Task Kdbx_turns_a_backup_into_a_database_KeePassXC_opens()
    {
        await MakeBackup();
        var file = Path.Combine(_dir, "vault.kdbx");
        const string kdbxPassword = "keepass password from the backup";
        Assert.Equal(0, (await Run(["kdbx", BackupFolder, file], Password, kdbxPassword, kdbxPassword)).Code);

        var start = new System.Diagnostics.ProcessStartInfo(VaultKdbxTests.Cli!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "show", "-s", "-a", "Password", file, "Logins/Bank" }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.StandardInput.WriteLineAsync(kdbxPassword);
        process.StandardInput.Close();
        var shown = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal("hunter2", shown.Trim());
    }

    private static async Task<(int Code, string Output)> Run(string[] args, params string[] secrets)
    {
        var queue = new Queue<string>(secrets);
        using var output = new StringWriter();
        var code = await new RestoreTool(output, _ => queue.Count > 0 ? queue.Dequeue() : "").RunAsync(args);
        return (code, output.ToString());
    }

    private async Task<(string Recovery, byte[] Document)> MakeBackup()
    {
        var paths = new HelmPaths(Path.Combine(_dir, "pc"));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory);
        var engine = Own(new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), [], debounce: TimeSpan.FromHours(1), blobs: blobs));
        var records = Own(new SyncedCollection<VaultItemRecord>(engine, VaultStore.Options));
        var keyrings = Own(new SyncedCollection<VaultKeyringData>(engine, new SyncedCollectionOptions<VaultKeyringData> { Name = VaultSession.KeyringCollection }));
        var settings = Own(new SettingsStoreFactory(paths));
        Directory.CreateDirectory(BackupFolder);
        settings.Get<VaultSettings>(VaultSettings.StoreId).Update(s => s.BackupLocation = BackupFolder);
        var session = Own(new VaultSession(keyrings, settings, new NoDeviceUnlock(), engine) { NewKdf = VaultCryptoTests.CheapKdf });
        var recovery = await session.CreateAsync(Password);
        var store = Own(new VaultStore(records, session));
        var files = new VaultFiles(store, blobs, session);

        var bank = store.Add(VaultItem.New(VaultItemKind.Login, "Bank") with { Fields = [new VaultField("Password", "old", VaultFieldKind.Password)] });
        await Task.Delay(5);
        store.Save(bank, store.Get(bank)!.Item with { Fields = [new VaultField("Password", "hunter2", VaultFieldKind.Password)] });
        var doc = store.Add(VaultItem.New(VaultItemKind.Document, "Passport"));
        var data = RandomNumberGenerator.GetBytes(BlobStore.ChunkSize + 1);
        await files.AttachAsync(doc, "pass/port?.pdf", "application/pdf", new MemoryStream(data));
        await new VaultBackupService(session, store, blobs, new FolderBackupLocation(), settings).BackUpAsync();
        return (recovery, data);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }
}
