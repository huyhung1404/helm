using System.Security.Cryptography;
using System.Text;
using Helm.Core.Linux;
using Helm.Core.Sync;

namespace Helm.Tests;

/// <summary>helm's secrets at rest on Linux: a host key under the data folder, optionally sealed by systemd-creds.</summary>
public sealed class LinuxSecretsTests : IDisposable
{
    private readonly TempDir _dir = new();

    private string Secrets => Path.Combine(_dir.Path, "secrets");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ProtectsForOnePurposeOnly()
    {
        var protector = new FileKeySecretProtector(Secrets);
        var data = Encoding.UTF8.GetBytes("helm_pat_secret");

        var sealedData = protector.Protect(data, "Helm.Sync.Credentials.v1");

        Assert.DoesNotContain("helm_pat", Encoding.UTF8.GetString(sealedData), StringComparison.Ordinal);
        Assert.Equal(data, protector.Unprotect(sealedData, "Helm.Sync.Credentials.v1"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(sealedData, "Helm.Sync.MasterKey.v1"));
        sealedData[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(sealedData, "Helm.Sync.Credentials.v1"));
    }

    [Fact]
    public void TheKeyIsMadeOnceAndKeptForTheNextRun()
    {
        var first = new FileKeySecretProtector(Secrets);
        Assert.Equal("none yet (made on first use)", first.KeySource);
        var sealedData = first.Protect([1, 2, 3], "p");

        Assert.Equal(32, File.ReadAllBytes(first.KeyFile).Length);
        Assert.Contains("mode 0600", first.KeySource, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], new FileKeySecretProtector(Secrets).Unprotect(sealedData, "p"));
        // Another box's key does not open it.
        var otherBox = new FileKeySecretProtector(Path.Combine(_dir.Path, "other"));
        otherBox.Protect([0], "p");
        Assert.ThrowsAny<CryptographicException>(() => otherBox.Unprotect(sealedData, "p"));
    }

    [Fact]
    public void ReadingWithoutAKeyNeverMakesOne()
    {
        var protector = new FileKeySecretProtector(Secrets);
        var stranger = new PlainSecretProtector().Protect([1], "p");

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect([.. "HLK1"u8, .. new byte[40]], "p"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(stranger, "p"));
        Assert.False(Directory.Exists(Secrets));
    }

    [Fact]
    public void KeepsTheSyncStoresKeys()
    {
        var protector = new FileKeySecretProtector(Secrets);
        var file = Path.Combine(_dir.Path, "sync", "master.key");
        new ProtectedMasterKeyStore(file, protector).Save(new byte[32]);

        Assert.Equal(new byte[32], new ProtectedMasterKeyStore(file, new FileKeySecretProtector(Secrets)).Load());
    }

    [Fact]
    public void ASealedKeyIsWrittenSealedAndUnsealedOnTheNextRun()
    {
        var sealer = new FakeSealer();
        var protector = new FileKeySecretProtector(Secrets, sealer, seal: true);
        var sealedData = protector.Protect([7], "p");

        Assert.True(File.Exists(protector.SealedKeyFile));
        Assert.False(File.Exists(protector.KeyFile));
        Assert.Equal(1, sealer.Sealed);
        Assert.Equal([7], new FileKeySecretProtector(Secrets, sealer).Unprotect(sealedData, "p"));
        Assert.Equal(1, sealer.Unsealed);
        // Without systemd-creds the sealed key cannot be read, and no other key takes its place.
        Assert.ThrowsAny<CryptographicException>(() => new FileKeySecretProtector(Secrets).Protect([1], "p"));
        Assert.False(File.Exists(protector.KeyFile));
    }

    [Fact]
    public void WithoutAUsableSealerTheKeyIsAPlainFile()
    {
        var protector = new FileKeySecretProtector(Secrets, new FakeSealer { Why = "this box has no usable TPM2" }, seal: true);
        protector.Protect([1], "p");

        Assert.True(File.Exists(protector.KeyFile));
        Assert.False(File.Exists(protector.SealedKeyFile));
    }

    [Fact]
    public void AServicesSystemdCredentialComesFirst()
    {
        var credentials = Path.Combine(_dir.Path, "credentials");
        Directory.CreateDirectory(credentials);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(Path.Combine(credentials, FileKeySecretProtector.CredentialName), key);
        var service = new FileKeySecretProtector(Secrets, credentialsDirectory: credentials);

        var sealedData = service.Protect([5], "p");

        Assert.StartsWith("systemd credential", service.KeySource, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Secrets));
        // The same key written as the plain file (as on a box before it was sealed) opens it too.
        Directory.CreateDirectory(Secrets);
        File.WriteAllBytes(Path.Combine(Secrets, FileKeySecretProtector.KeyFileName), key);
        Assert.Equal([5], new FileKeySecretProtector(Secrets).Unprotect(sealedData, "p"));
    }

    [Fact]
    public void AKeyOfTheWrongSizeIsRefused()
    {
        Directory.CreateDirectory(Secrets);
        File.WriteAllBytes(Path.Combine(Secrets, FileKeySecretProtector.KeyFileName), new byte[16]);

        Assert.ThrowsAny<CryptographicException>(() => new FileKeySecretProtector(Secrets).Protect([1], "p"));
    }

    [Fact]
    public void FilesAndFoldersAreOnlyTheUsers()
    {
        if (OperatingSystem.IsWindows()) return; // Unix modes; on Windows the folder's ACL applies.
        var protector = new FileKeySecretProtector(Secrets);
        protector.Protect([1], "p");
        Assert.Equal(PrivateFiles.PrivateFolderMode, File.GetUnixFileMode(Secrets));
        Assert.Equal(PrivateFiles.PrivateFileMode, File.GetUnixFileMode(protector.KeyFile));

        File.SetUnixFileMode(protector.KeyFile, PrivateFiles.PrivateFileMode | UnixFileMode.OtherRead);
        new FileKeySecretProtector(Secrets).Protect([1], "p");
        Assert.Equal(PrivateFiles.PrivateFileMode, File.GetUnixFileMode(protector.KeyFile));
    }

    [Fact]
    public void WritingANewFileNeverReplacesOne()
    {
        var file = Path.Combine(_dir.Path, "key");
        Assert.True(PrivateFiles.WriteNew(file, [1]));
        Assert.False(PrivateFiles.WriteNew(file, [2]));
        Assert.Equal([1], File.ReadAllBytes(file));
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public void SystemdCredsSealsUnderHelmsCredentialName()
    {
        var calls = new List<string>();
        var sealer = new SystemdCredsSealer("tpm2", (args, input) =>
        {
            calls.Add(string.Join(' ', args));
            return args[0] switch
            {
                "has-tpm2" => (0, [], ""),
                "encrypt" => (0, [.. "sealed:"u8, .. input], ""),
                _ => (0, input[7..], ""),
            };
        });

        Assert.Null(sealer.Unavailable());
        var sealedKey = sealer.Seal([1, 2]);
        Assert.Equal([1, 2], sealer.Unseal(sealedKey));
        Assert.Equal(["has-tpm2 --quiet", "encrypt --with-key=tpm2 --name=helm-host-key - -", "decrypt --name=helm-host-key - -"], calls);
    }

    [Fact]
    public void SystemdCredsFailuresAreCryptographicOnes()
    {
        var sealer = new SystemdCredsSealer("tpm2", (args, _) => args[0] == "has-tpm2" ? (1, [], "") : (1, [], "Failed to unseal: TPM2 not available"));

        Assert.Equal("this box has no usable TPM2", sealer.Unavailable());
        var error = Assert.ThrowsAny<CryptographicException>(() => sealer.Unseal([1]));
        Assert.Contains("TPM2 not available", error.Message, StringComparison.Ordinal);
        // The host key (root's) needs no TPM.
        Assert.Null(new SystemdCredsSealer("host", (_, _) => (1, [], "")).Unavailable());
    }

    private sealed class FakeSealer : IHostKeySealer
    {
        public string? Why { get; init; }

        public int Sealed { get; private set; }

        public int Unsealed { get; private set; }

        public string? Unavailable() => Why;

        public byte[] Seal(byte[] key)
        {
            Sealed++;
            return [.. key.Select(b => (byte)(b ^ 0x5A))];
        }

        public byte[] Unseal(byte[] sealedKey)
        {
            Unsealed++;
            return [.. sealedKey.Select(b => (byte)(b ^ 0x5A))];
        }
    }
}
