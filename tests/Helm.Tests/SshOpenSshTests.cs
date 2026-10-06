using System.Security.Cryptography;
using System.Text;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Ssh;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>Using what OpenSSH already has on the device: ~/.ssh/config, known_hosts and key files.</summary>
public sealed class SshOpenSshTests : IDisposable
{
    private const string Ed25519Blob = "AAAAC3NzaC1lZDI1NTE5AAAAICKovDisqmssVHliJWei/YjMb8aMPg8qFogDzxA5V5Pd";
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public SshOpenSshTests() => _settings = new SettingsStoreFactory(new HelmPaths(Path.Combine(_dir.Path, "helm")));

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Config_hosts_are_read_like_OpenSSH()
    {
        const string config = """
            # personal
            Host my-vps
                HostName 203.0.113.10
                User root
                Port 2200

            Host *
                ServerAliveInterval 30

            Host web web.alias
              HostName=example.org
              User "deploy"
              IdentityFile ~/.ssh/deploy_key
              IdentityFile ~/.ssh/second_key

            Host bare-alias
                User me

            Host no-user
                HostName example.net

            Match host example.com
                User nobody
            """;
        var home = Path.Combine(_dir.Path, "home");
        var hosts = SshOpenSsh.ParseConfig(config, home);
        Assert.Equal(3, hosts.Count);
        Assert.Equal(new SshConfigHost("my-vps", "203.0.113.10", 2200, "root", null), hosts[0]);
        Assert.Equal(new SshConfigHost("web", "example.org", 22, "deploy", Path.Combine(home, ".ssh", "deploy_key")), hosts[1]);
        Assert.Equal(new SshConfigHost("bare-alias", "bare-alias", 22, "me", null), hosts[2]);
    }

    [Fact]
    public void Without_an_IdentityFile_the_first_default_key_is_used()
    {
        var ssh = Directory.CreateDirectory(Path.Combine(_dir.Path, ".ssh")).FullName;
        var host = new SshConfigHost("a", "example.org", 22, "me", null);
        Assert.Null(SshOpenSsh.KeyFileFor(host, ssh));
        File.WriteAllText(Path.Combine(ssh, "id_ed25519"), "x");
        Assert.Equal(Path.Combine(ssh, "id_ed25519"), SshOpenSsh.KeyFileFor(host, ssh));
        File.WriteAllText(Path.Combine(ssh, "id_rsa"), "x");
        Assert.Equal(Path.Combine(ssh, "id_rsa"), SshOpenSsh.KeyFileFor(host, ssh));
        Assert.Equal("C:\\keys\\k", SshOpenSsh.KeyFileFor(host with { IdentityFile = "C:\\keys\\k" }, ssh));
    }

    [Fact]
    public void Known_hosts_give_the_keys_already_trusted_for_that_server()
    {
        var hashed = HashedName("[vps.example.org]:2200");
        var text = $"""
            [203.0.113.10]:2200 ssh-ed25519 {Ed25519Blob}
            203.0.113.10 ssh-ed25519 {Ed25519Blob}
            other.example.org,[203.0.113.10]:2200 ssh-ed25519 {Ed25519Blob} comment here
            @revoked [203.0.113.10]:2200 ssh-ed25519 {Ed25519Blob}
            [203.0.113.10]:2200 ssh-rsa {Ed25519Blob}
            [203.0.113.10]:2200 ssh-ed25519 not-base64!!
            {hashed} ssh-ed25519 {Ed25519Blob}
            """;
        var keys = SshOpenSsh.KnownKeys(text, "203.0.113.10", 2200);
        // Line 1 and line 3; the revoked line, the blob that is not an RSA key and the broken line are left out.
        Assert.Equal(2, keys.Count);
        var fingerprint = Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(Ed25519Blob))).TrimEnd('=');
        Assert.All(keys, k => Assert.Equal(new SshHostKey("203.0.113.10", 2200, "ssh-ed25519", fingerprint), k));
        // Port 22 is written without brackets; hashed names match too.
        Assert.Single(SshOpenSsh.KnownKeys(text, "203.0.113.10", 22));
        Assert.Single(SshOpenSsh.KnownKeys(text, "vps.example.org", 2200));
        Assert.Empty(SshOpenSsh.KnownKeys(text, "vps.example.org", 22));
    }

    [Fact]
    public void Importing_adds_the_servers_with_their_keys_and_trust_once()
    {
        var ssh = Directory.CreateDirectory(Path.Combine(_dir.Path, "home", ".ssh")).FullName;
        File.WriteAllText(Path.Combine(ssh, "config"), "Host my-vps\n    HostName 203.0.113.10\n    User root\n    Port 2200\n\nHost nokey\n    HostName example.org\n    User me\n    IdentityFile ~/.ssh/missing\n");
        File.WriteAllText(Path.Combine(ssh, "known_hosts"), $"[203.0.113.10]:2200 ssh-ed25519 {Ed25519Blob}\n");
        File.WriteAllText(Path.Combine(ssh, "id_rsa"), "not read by the import");
        using var vm = NewViewModel();
        vm.ImportOpenSsh(ssh);
        Assert.Equal(2, vm.Hosts.Count);
        var vps = vm.Hosts[0].Host;
        Assert.Equal(("my-vps", "root@203.0.113.10:2200", SshAuthKind.KeyFile, Path.Combine(ssh, "id_rsa")), (vps.Name, vps.Target, vps.Auth, vps.KeyFile));
        Assert.Equal(Path.Combine(ssh, "missing"), vm.Hosts[1].Host.KeyFile);
        Assert.Same(vm.Hosts[0], vm.SelectedHost);
        var known = Assert.Single(vm.KnownHosts);
        Assert.Equal("203.0.113.10:2200", known.Title);
        Assert.Contains("1 server key", vm.Message, StringComparison.Ordinal);
        Assert.Equal("Key id_rsa", vm.Hosts[0].AuthText);

        // Again: nothing doubles.
        vm.ImportOpenSsh(ssh);
        Assert.Equal(2, vm.Hosts.Count);
        Assert.Single(vm.KnownHosts);
        Assert.Contains("Nothing new", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Importing_without_a_config_says_so()
    {
        using var vm = NewViewModel();
        vm.ImportOpenSsh(Path.Combine(_dir.Path, "nothing"));
        Assert.Empty(vm.Hosts);
        Assert.Contains("No SSH config", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_with_a_passphrase_asks_for_it()
    {
        var keyFile = Path.Combine(_dir.Path, "id_rsa");
        using (var rsa = RSA.Create(2048))
        {
            File.WriteAllText(keyFile, rsa.ExportEncryptedPkcs8PrivateKeyPem("correct horse",
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
        }
        using var vm = NewViewModel();
        vm.NewHostCommand.Execute(null);
        vm.EditAddress = "me@127.0.0.1:1";
        vm.EditAuthIndex = (int)SshAuthKind.KeyFile;
        Assert.True(vm.IsKeyFileAuth);
        vm.EditKeyFile = "relative\\path";
        vm.SaveEditCommand.Execute(null);
        Assert.NotNull(vm.EditError);
        vm.EditKeyFile = '"' + keyFile + '"';
        vm.SaveEditCommand.Execute(null);
        Assert.Null(vm.EditError);
        Assert.Equal(keyFile, vm.Hosts[0].Host.KeyFile);
        Assert.False(vm.NeedsPassword);
        Assert.Equal("Key passphrase", vm.PasswordPrompt);

        await vm.ConnectAsync(null);
        Assert.Contains("passphrase", vm.Message, StringComparison.Ordinal);
        Assert.True(vm.NeedsPassword);
        Assert.Null(vm.ActiveSession);

        await vm.ConnectAsync("wrong horse");
        Assert.Contains("passphrase may be wrong", vm.Message, StringComparison.Ordinal);
        Assert.True(vm.NeedsPassword);

        // With the passphrase the key opens and Helm goes on to connect (port 1 refuses).
        await vm.ConnectAsync("correct horse");
        Assert.Contains("refused the connection", vm.Message, StringComparison.Ordinal);

        // The settings keep the path only.
        var store = _settings.Get<SshSettings>(SshIds.ModuleId);
        store.Flush();
        var json = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_key_file_is_reported()
    {
        var keyFile = Path.Combine(_dir.Path, "gone");
        File.WriteAllText(keyFile, "x");
        using var vm = NewViewModel();
        vm.EditAddress = "me@127.0.0.1:1";
        vm.EditAuthIndex = (int)SshAuthKind.KeyFile;
        vm.EditKeyFile = keyFile;
        vm.SaveEditCommand.Execute(null);
        File.Delete(keyFile);
        await vm.ConnectAsync(null);
        Assert.Contains("was not found", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_can_sign_in_with_a_Vault_field()
    {
        var vault = new FakeVault { Unlocked = false };
        using var vm = NewViewModel(vault);
        vm.NewHostCommand.Execute(null);
        vm.EditAddress = "root@127.0.0.1:1";
        vm.EditName = "VPS";
        vm.EditAuthIndex = (int)SshAuthKind.Vault;
        Assert.True(vm.IsVaultAuth);
        Assert.True(vm.IsVaultLocked);
        Assert.Empty(vm.VaultFields);
        vm.SaveEditCommand.Execute(null);
        Assert.Contains("Unlock Vault", vm.EditError, StringComparison.Ordinal);

        // Unlocking from the editor lists the fields by name; no value is ever read for the list.
        vault.QuickUnlockWorks = true;
        await vm.UnlockVaultCommand.ExecuteAsync(null);
        Assert.False(vm.IsVaultLocked);
        Assert.Equal(["VPS · token", "VPS · user"], vm.VaultFields.Select(f => f.DisplayName));
        Assert.Equal(0, vault.Reads);
        vm.EditVaultRef = vm.VaultFields[0];
        vm.SaveEditCommand.Execute(null);
        var host = Assert.Single(vm.Hosts).Host;
        Assert.Equal(("uid-1", "VPS", "token"), (host.VaultItemUid, host.VaultItemTitle, host.VaultField));
        Assert.Equal("Vault: VPS · token", vm.Hosts[0].AuthText);
        Assert.False(vm.NeedsPassword);

        // Locked, no quick unlock: nothing is read, the page offers to open Vault.
        vault.Unlocked = false;
        vault.QuickUnlockWorks = false;
        await vm.ConnectAsync(null);
        Assert.True(vm.CanOpenVault);
        Assert.Contains("Unlock Vault", vm.Message, StringComparison.Ordinal);
        Assert.Equal(0, vault.Reads);
        Assert.Null(vm.ActiveSession);
        vm.OpenVaultCommand.Execute(null);
        Assert.Equal(1, vault.Shown);
        Assert.False(vm.CanOpenVault);

        // Quick unlock works: the one field is read and used (port 1 then refuses), and nothing lands in the settings.
        vault.QuickUnlockWorks = true;
        await vm.ConnectAsync(null);
        Assert.Equal(1, vault.Reads);
        Assert.Contains("refused the connection", vm.Message, StringComparison.Ordinal);
        var store = _settings.Get<SshSettings>(SshIds.ModuleId);
        store.Flush();
        Assert.DoesNotContain("vault-token-value", File.ReadAllText(store.FilePath), StringComparison.Ordinal);

        // The field was removed from Vault: said plainly.
        vault.Values.Remove(("uid-1", "token"));
        await vm.ConnectAsync(null);
        Assert.Contains("no field token", vm.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Vault_value_is_used_as_a_password_or_as_a_private_key()
    {
        var key = new SshDeviceKey(Path.Combine(_dir.Path, "k.bin"), new PlainSecretProtector(), "pc");
        var host = new SshHost { User = "root", Address = "example.org", Auth = SshAuthKind.Vault, VaultItemUid = "u", VaultField = "token" };
        Assert.Equal(["password", "keyboard-interactive"], SshConnection.Create(host, key, null, "a-token").AuthenticationMethods.Select(m => m.Name));
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        Assert.True(SshConnection.IsPrivateKey("\n  " + pem));
        Assert.Equal(["publickey"], SshConnection.Create(host, key, null, pem).AuthenticationMethods.Select(m => m.Name));
        Assert.Throws<ArgumentException>(() => SshConnection.Create(host, key, null, ""));
        Assert.False(key.Exists);
    }

    private sealed class FakeVault : Helm.Core.Secrets.IVaultSecrets
    {
        public bool Unlocked { get; set; } = true;

        public bool QuickUnlockWorks { get; set; }

        public int Reads { get; private set; }

        public int Shown { get; private set; }

        public Dictionary<(string, string), string> Values { get; } = new()
        {
            [("uid-1", "token")] = "vault-token-value",
            [("uid-1", "user")] = "root",
        };

        public bool IsUnlocked => Unlocked;

        public Task<bool> TryQuickUnlockAsync(CancellationToken ct = default)
        {
            if (QuickUnlockWorks) Unlocked = true;
            return Task.FromResult(Unlocked);
        }

        public void ShowVault() => Shown++;

        public IReadOnlyList<Helm.Core.Secrets.VaultSecretRef> ListFields() =>
            Unlocked ? Values.Keys.Select(k => new Helm.Core.Secrets.VaultSecretRef(k.Item1, "VPS", k.Item2)).OrderBy(r => r.FieldName).ToList() : [];

        public string? Read(string itemUid, string fieldName)
        {
            if (!Unlocked) return null;
            Reads++;
            return Values.GetValueOrDefault((itemUid, fieldName));
        }
    }

    private static string HashedName(string name)
    {
        var salt = RandomNumberGenerator.GetBytes(20);
        return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(HMACSHA1.HashData(salt, Encoding.ASCII.GetBytes(name)))}";
    }

    private SshViewModel NewViewModel(Helm.Core.Secrets.IVaultSecrets? vault = null)
    {
        var key = new SshDeviceKey(Path.Combine(_dir.Path, "helm", "settings", "ssh", "device-key.bin"), new PlainSecretProtector(), "pc");
        return new SshViewModel(_settings, key, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), NullLogger<SshViewModel>.Instance, vault);
    }
}
