using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Platform;

namespace Helm.Tests;

/// <summary>The key Windows Hello quick unlock wraps the vault key with, and hello.bin files from before the salt.</summary>
public sealed class WindowsHelloUnlockTests
{
    private static readonly byte[] Signature = RandomNumberGenerator.GetBytes(256);

    [Fact]
    public void Files_without_a_salt_keep_the_key_they_were_written_with()
    {
        // The derivation of Helm 0.9-0.23: an existing hello.bin must still open after the update.
        var old = SHA256.HashData([.. Encoding.UTF8.GetBytes("helm-vault/hello|"), .. Signature]);
        Assert.Equal(old, WindowsHelloUnlock.KekOf(Signature, salt: null));
    }

    [Fact]
    public void A_salted_key_is_hkdf_of_the_signature_and_differs_per_salt()
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var expected = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Signature, expected, salt, Encoding.UTF8.GetBytes("helm-vault/hello/v2"));

        Assert.Equal(expected, WindowsHelloUnlock.KekOf(Signature, salt));
        Assert.Equal(WindowsHelloUnlock.KekOf(Signature, salt), WindowsHelloUnlock.KekOf(Signature, salt));
        Assert.NotEqual(WindowsHelloUnlock.KekOf(Signature, salt), WindowsHelloUnlock.KekOf(Signature, RandomNumberGenerator.GetBytes(32)));
        Assert.NotEqual(WindowsHelloUnlock.KekOf(Signature, null), WindowsHelloUnlock.KekOf(Signature, salt));
    }

    [Fact]
    public void An_old_hello_file_parses_without_a_salt_and_its_vault_key_still_opens()
    {
        var vaultKey = RandomNumberGenerator.GetBytes(32);
        var oldKek = WindowsHelloUnlock.KekOf(Signature, null);
        var oldJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            VaultId = "v1",
            Challenge = new byte[] { 1, 2, 3 },
            Wrapped = VaultCrypto.Seal(oldKek, vaultKey, "helm-vault/v1/hello|v1"),
        });

        var stored = WindowsHelloUnlock.Parse(oldJson)!;
        Assert.Null(stored.Salt);
        Assert.Equal(vaultKey, VaultCrypto.Open(WindowsHelloUnlock.KekOf(Signature, stored.Salt), stored.Wrapped, "helm-vault/v1/hello|v1"));

        var upgraded = WindowsHelloUnlock.Parse(JsonSerializer.SerializeToUtf8Bytes(stored with { Salt = [9, 9] }))!;
        Assert.Equal([9, 9], upgraded.Salt!);
    }
}
