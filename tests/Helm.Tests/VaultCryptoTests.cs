using System.Security.Cryptography;
using Helm.Core.Sync;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;

namespace Helm.Tests;

public sealed class VaultCryptoTests
{
    internal static VaultKdf CheapKdf() => new(VaultKdf.Argon2id, 8 * 1024, 1, 1, RandomNumberGenerator.GetBytes(16));
    private const string Password = "correct horse battery staple";

    [Fact]
    public void Recovery_key_round_trips_and_forgives_case_dashes_and_look_alikes()
    {
        var (secret, text) = VaultRecoveryKey.Create();
        Assert.StartsWith(VaultRecoveryKey.Prefix, text);
        Assert.Equal(6 + 14 * 4 + 13, text.Length);
        Assert.Equal(secret, VaultRecoveryKey.Parse(text));
        Assert.Equal(secret, VaultRecoveryKey.Parse(text.ToLowerInvariant().Replace("-", " ")));
        Assert.Equal(secret, VaultRecoveryKey.Parse(text[6..].Replace('0', 'O').Replace('1', 'l')));
    }

    [Fact]
    public void A_mistyped_recovery_key_is_caught_by_the_checksum()
    {
        var (_, text) = VaultRecoveryKey.Create();
        var index = text.Length - 10;
        var typo = text[..index] + (text[index] == 'A' ? 'B' : 'A') + text[(index + 1)..];
        Assert.Null(VaultRecoveryKey.Parse(typo));
        Assert.Null(VaultRecoveryKey.Parse(text[..^1]));
        Assert.Null(VaultRecoveryKey.Parse("HELM-ABCD"));
        Assert.Null(VaultRecoveryKey.Parse(""));
    }

    [Fact]
    public void Recovery_id_is_stable_and_differs_between_keys()
    {
        var (a, _) = VaultRecoveryKey.Create();
        var (b, _) = VaultRecoveryKey.Create();
        Assert.Equal(VaultRecoveryKey.IdOf(a), VaultRecoveryKey.IdOf(a));
        Assert.NotEqual(VaultRecoveryKey.IdOf(a), VaultRecoveryKey.IdOf(b));
        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}$", VaultRecoveryKey.IdOf(a));
    }

    [Fact]
    public void A_new_keyring_opens_with_the_password_and_the_recovery_key()
    {
        var created = VaultKeyring.Create(Password, 1, CheapKdf());
        using var byPassword = VaultKeyring.UnlockWithPassword(created.Keyring, Password);
        using var byRecovery = VaultKeyring.UnlockWithRecoveryKey(created.Keyring, created.RecoveryKey);
        Assert.Equal(created.Key.Bytes.ToArray(), byPassword.Bytes.ToArray());
        Assert.Equal(created.Key.Bytes.ToArray(), byRecovery.Bytes.ToArray());
        Assert.True(created.Keyring.Matches(byPassword));
        created.Key.Dispose();
    }

    [Fact]
    public void Wrong_password_and_foreign_recovery_keys_are_refused()
    {
        var created = VaultKeyring.Create(Password, 1, CheapKdf());
        var other = VaultKeyring.Create(Password, 1, CheapKdf());
        Assert.Throws<VaultKeyException>(() => VaultKeyring.UnlockWithPassword(created.Keyring, Password + "!"));
        var ex = Assert.Throws<VaultKeyException>(() => VaultKeyring.UnlockWithRecoveryKey(created.Keyring, other.RecoveryKey));
        Assert.Contains("another vault", ex.Message);
        Assert.False(created.Keyring.Matches(other.Key));
    }

    [Fact]
    public void Weak_passwords_are_refused()
    {
        Assert.Throws<VaultKeyException>(() => VaultKeyring.Create("short", 1, CheapKdf()));
        Assert.Throws<VaultKeyException>(() => VaultKeyring.Create("aaaaaaaaaaaaaaaaaaaa", 1, CheapKdf()));
    }

    [Fact]
    public void Changing_the_password_keeps_the_key_and_the_recovery_key()
    {
        var created = VaultKeyring.Create(Password, 1, CheapKdf());
        var changed = VaultKeyring.ChangePassword(created.Keyring, created.Key, "a brand new vault password 7", 2);

        Assert.Throws<VaultKeyException>(() => VaultKeyring.UnlockWithPassword(changed, Password));
        using var byNew = VaultKeyring.UnlockWithPassword(changed, "a brand new vault password 7");
        using var byRecovery = VaultKeyring.UnlockWithRecoveryKey(changed, created.RecoveryKey);
        Assert.Equal(created.Key.Bytes.ToArray(), byNew.Bytes.ToArray());
        // The cheap test parameters count as a tampered, weakened keyring: a password change restores today's cost.
        Assert.Equal(VaultKdf.Default().MemoryKib, changed.Kdf.MemoryKib);
        Assert.Equal(VaultKdf.Default().Passes, changed.Kdf.Passes);
        Assert.NotEqual(created.Keyring.Kdf.Salt, changed.Kdf.Salt);
    }

    [Fact]
    public void A_new_recovery_key_retires_the_old_kit()
    {
        var created = VaultKeyring.Create(Password, 1, CheapKdf());
        var (changed, text) = VaultKeyring.NewRecoveryKey(created.Keyring, created.Key, 2);
        Assert.NotEqual(created.Keyring.RecoveryId, changed.RecoveryId);
        Assert.Throws<VaultKeyException>(() => VaultKeyring.UnlockWithRecoveryKey(changed, created.RecoveryKey));
        using var byNew = VaultKeyring.UnlockWithRecoveryKey(changed, text);
        using var byPassword = VaultKeyring.UnlockWithPassword(changed, Password);
    }

    [Fact]
    public void Keyrings_from_a_newer_Helm_are_refused_not_misread()
    {
        var created = VaultKeyring.Create(Password, 1, CheapKdf());
        Assert.Throws<VaultKeyException>(() => VaultKeyring.UnlockWithPassword(created.Keyring with { V = 2 }, Password));
        Assert.Throws<VaultKeyException>(() => VaultKeyring.UnlockWithPassword(created.Keyring with { Kdf = created.Keyring.Kdf with { Alg = "argon3" } }, Password));
    }

    [Fact]
    public void A_locked_key_is_zeroed_and_unusable()
    {
        var key = VaultKey.Create();
        key.Dispose();
        Assert.Throws<ObjectDisposedException>(() => key.Bytes.Length);
        Assert.Equal("VaultKey(***)", key.ToString());
    }

    [Fact]
    public void Sealed_items_open_only_with_the_same_key_and_envelope()
    {
        var created = VaultKeyring.Create(Password, 1, CheapKdf());
        var blob = new BlobRef("01JBLOB0000000000000000000", 10, 4096, 1, new byte[32]);
        var item = VaultItem.New(VaultItemKind.Login, "Bank") with
        {
            Attachments = [new VaultAttachment("a", "id.pdf", "application/pdf", 10, blob)],
        };
        var record = VaultItemSealer.Seal(created.Key, "vault1", 1, "uid1", item, trashed: false, trashedAtMs: null);

        Assert.Equal(["01JBLOB0000000000000000000"], record.Blobs);
        Assert.Equal("Bank", VaultItemSealer.Open(created.Key, record).Title);
        // Each readable field is bound on its own: flipping any one of them breaks the seal.
        Assert.Throws<VaultKeyException>(() => VaultItemSealer.Open(created.Key, record with { Trashed = true }));
        Assert.Throws<VaultKeyException>(() => VaultItemSealer.Open(created.Key, record with { TrashedAtMs = 5 }));
        Assert.Throws<VaultKeyException>(() => VaultItemSealer.Open(created.Key, record with { Epoch = 2 }));
        Assert.Throws<VaultKeyException>(() => VaultItemSealer.Open(created.Key, record with { Vault = "vault2" }));
        Assert.Throws<VaultKeyException>(() => VaultItemSealer.Open(created.Key, record with { Blobs = [] }));
        Assert.Throws<VaultKeyException>(() => VaultItemSealer.Open(created.Key, record with { Uid = "uid2" }));
        Assert.Throws<VaultKeyException>(() => VaultItemSealer.Open(created.Key, record with { V = 2 }));
        using var other = VaultKey.Create();
        Assert.Throws<VaultKeyException>(() => VaultItemSealer.Open(other, record));
    }

    [Fact]
    public void Secrets_never_appear_in_ToString()
    {
        var field = new VaultField("Password", "hunter2-super-secret", VaultFieldKind.Password);
        var item = VaultItem.New(VaultItemKind.Login, "Bank") with { Fields = [field], Notes = "pin 1234" };
        Assert.DoesNotContain("hunter2", field.ToString());
        Assert.DoesNotContain("hunter2", item.ToString());
        Assert.DoesNotContain("1234", item.ToString());
        Assert.DoesNotContain("Bank", item.ToString());
    }

    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(128)]
    public void Generated_passwords_have_the_length_and_every_chosen_class(int length)
    {
        for (var i = 0; i < 50; i++)
        {
            var password = PasswordGenerator.Generate(new PasswordOptions(length));
            Assert.Equal(length, password.Length);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, c => !char.IsLetterOrDigit(c));
            Assert.DoesNotContain(password, c => "Il1O0o".Contains(c));
        }
        Assert.Equal(20, PasswordGenerator.Generate(new PasswordOptions(20, Lowercase: false, Uppercase: false, Digits: true, Symbols: false)).Count(char.IsDigit));
        Assert.True(PasswordGenerator.EntropyBits(new PasswordOptions(20)) > 110);
    }
}
