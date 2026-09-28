using System.Diagnostics;
using System.Security.Cryptography;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Tests;

/// <summary>Runs only when HELM_KEEPASSXC_CLI points at keepassxc-cli(.exe), e.g. from the KeePassXC portable zip.</summary>
public sealed class KeePassXcFactAttribute : FactAttribute
{
    public KeePassXcFactAttribute()
    {
        if (VaultKdbxTests.Cli is null) Skip = "Set HELM_KEEPASSXC_CLI to keepassxc-cli to check KDBX exports with KeePassXC.";
    }
}

public sealed class VaultKdbxTests : IDisposable
{
    internal static readonly string? Cli = Environment.GetEnvironmentVariable("HELM_KEEPASSXC_CLI") is { Length: > 0 } path && File.Exists(path) ? path : null;
    private const string ExportPassword = "export password for keepass 2026";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ChaCha20_matches_the_RFC_8439_test_vector()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var nonce = Convert.FromHexString("000000000000004a00000000");
        var data = "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it."u8.ToArray();
        new ChaCha20(key, nonce, counter: 1).Apply(data);
        Assert.Equal(
            "6e2e359a2568f98041ba0728dd0d6981e97e7aec1d4360c20a27afccfd9fae0bf91b65c5524733ab8f593dabcd62b3571639d624e65152ab8f530c359f0861d807ca0dbf500d6a6156a38e088a22b65e52bc514d16ccf806818ce91ab77937365af90bbf74a35be6b40b8eedf2785e42874d",
            Convert.ToHexString(data).ToLowerInvariant());
    }

    [Fact]
    public void ChaCha20_streams_across_calls_like_one_call()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var whole = new byte[200];
        new ChaCha20(key, nonce).Apply(whole);
        var parts = new byte[200];
        var stream = new ChaCha20(key, nonce);
        stream.Apply(parts.AsSpan(0, 7));
        stream.Apply(parts.AsSpan(7, 100));
        stream.Apply(parts.AsSpan(107));
        Assert.Equal(whole, parts);
    }

    [Fact]
    public async Task The_export_refuses_a_weak_password()
    {
        var (_, exporter) = await NewVault();
        await Assert.ThrowsAsync<VaultKeyException>(() => exporter.ExportAsync(new MemoryStream(), new KdbxExportOptions("short")));
    }

    [KeePassXcFact]
    public async Task KeePassXC_opens_the_export_with_passwords_fields_history_and_documents()
    {
        var (store, exporter) = await NewVault();
        var bank = store.Add(VaultItem.New(VaultItemKind.Login, "Bank") with
        {
            Fields =
            [
                new VaultField("Username", "anh@example.com", VaultFieldKind.Username),
                new VaultField("Password", "old-password", VaultFieldKind.Password),
                new VaultField("Website", "https://bank.example", VaultFieldKind.Url),
                new VaultField("PIN", "4242", VaultFieldKind.Secret),
            ],
            Notes = "Ghi chú tiếng Việt ✨",
        });
        await Task.Delay(5);
        var edited = store.Get(bank)!.Item;
        store.Save(bank, edited with { Fields = [.. edited.Fields.Select(f => f.Kind == VaultFieldKind.Password ? f with { Value = "hunter2 & <new>" } : f)] });
        store.Add(VaultItem.New(VaultItemKind.Card, "Visa") with { Fields = [new VaultField("Number", "4111 1111 1111 1111", VaultFieldKind.Secret), new VaultField("Number", "second")] });
        store.Add(VaultItem.New(VaultItemKind.Token, "OpenAI API") with { Fields = [new VaultField("Token", "sk-test-123", VaultFieldKind.Secret)] });
        var doc = store.Add(VaultItem.New(VaultItemKind.Document, "Passport"));
        var data = RandomNumberGenerator.GetBytes(200_000);
        await _files!.AttachAsync(doc, "passport.pdf", "application/pdf", new MemoryStream(data));

        var file = Path.Combine(_dir, "vault.kdbx");
        await using (var output = File.Create(file))
            Assert.Equal(4, await exporter.ExportAsync(output, new KdbxExportOptions(ExportPassword), new KdbxKdf(8 * 1024 * 1024, 2, 1), default));

        var list = Run("ls", "-R", "-f", file);
        Assert.Contains("Logins/", list);
        Assert.Contains("Bank", list);
        Assert.Contains("Cards/", list);
        Assert.Contains("Documents/", list);

        Assert.Equal("hunter2 & <new>", Run("show", "-s", "-a", "Password", file, "Logins/Bank").Trim());
        Assert.Equal("anh@example.com", Run("show", "-a", "UserName", file, "Logins/Bank").Trim());
        Assert.Equal("https://bank.example", Run("show", "-a", "URL", file, "Logins/Bank").Trim());
        Assert.Equal("4242", Run("show", "-s", "-a", "PIN", file, "Logins/Bank").Trim());
        Assert.Equal("Ghi chú tiếng Việt ✨", Run("show", "-a", "Notes", file, "Logins/Bank").Trim());
        Assert.Equal("4111 1111 1111 1111", Run("show", "-s", "-a", "Number", file, "Cards/Visa").Trim());
        Assert.Equal("second", Run("show", "-a", "Number (2)", file, "Cards/Visa").Trim());
        // A token is KeePass's (protected) password, not a custom field.
        Assert.Equal("sk-test-123", Run("show", "-s", "-a", "Password", file, "Tokens/OpenAI API").Trim());

        var exported = Path.Combine(_dir, "out.pdf");
        Run("attachment-export", file, "Documents/Passport", "passport.pdf", exported);
        Assert.Equal(data, await File.ReadAllBytesAsync(exported));

        var wrong = Run("ls", file, expectFailure: true, password: "not the password at all");
        Assert.DoesNotContain("Bank", wrong);
    }

    private static string Run(string command, params string[] args) => Run(command, args, expectFailure: false, password: ExportPassword);

    private static string Run(string command, string file, bool expectFailure, string password) => Run(command, [file], expectFailure, password);

    private static string Run(string command, string[] args, bool expectFailure, string password)
    {
        var start = new ProcessStartInfo(Cli!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        start.ArgumentList.Add(command);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.WriteLine(password);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "keepassxc-cli did not finish");
        if (!expectFailure) Assert.True(process.ExitCode == 0, $"keepassxc-cli {command} failed: {error}");
        else Assert.NotEqual(0, process.ExitCode);
        return output;
    }

    private VaultFiles? _files;

    private async Task<(VaultStore Store, VaultKdbxExporter Exporter)> NewVault()
    {
        var paths = new HelmPaths(Path.Combine(_dir, "pc"));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory);
        var engine = Own(new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), [], debounce: TimeSpan.FromHours(1), blobs: blobs));
        var records = Own(new SyncedCollection<VaultItemRecord>(engine, VaultStore.Options));
        var keyrings = Own(new SyncedCollection<VaultKeyringData>(engine, new SyncedCollectionOptions<VaultKeyringData> { Name = VaultSession.KeyringCollection }));
        var session = Own(new VaultSession(keyrings, Own(new SettingsStoreFactory(paths)), new NoDeviceUnlock(), engine) { NewKdf = VaultCryptoTests.CheapKdf });
        await session.CreateAsync("correct horse battery staple");
        var store = Own(new VaultStore(records, session));
        _files = new VaultFiles(store, blobs, session);
        return (store, new VaultKdbxExporter(store, _files));
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }
}
