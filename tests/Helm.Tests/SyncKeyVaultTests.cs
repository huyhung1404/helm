using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helm.Core.Sync;

namespace Helm.Tests;

public sealed class SyncKeyVaultTests
{
    // Cheap Argon2id so tests stay fast; one test below checks the real default parameters.
    private static readonly SyncKeyVault.KdfParams Fast = new("argon2id", 0, 8 * 1024, 1, 1);
    private const string Pass = "correct horse battery staple";

    [Fact]
    public void Passphrase_and_recovery_key_both_open_the_same_keys()
    {
        var created = SyncKeyVault.Create(Pass, Fast);

        using var byPass = SyncKeyVault.UnlockWithPassphrase(created.KeyringData, Pass);
        using var byRecovery = SyncKeyVault.UnlockWithRecoveryKey(created.KeyringData, created.RecoveryKey);
        Assert.Equal(1, byPass.CurrentEpoch);
        Assert.Equal(created.Keys.Current, byPass.Current);
        Assert.Equal(created.Keys.Current, byRecovery.Current);
        Assert.False(SyncKeyVault.NeedsUpgrade(created.KeyringData));
    }

    [Fact]
    public void Default_protection_is_argon2id_128_mib()
    {
        var created = SyncKeyVault.Create(Pass);
        using var doc = JsonDocument.Parse(created.KeyringData);
        var kdf = doc.RootElement.GetProperty("kdf");
        Assert.Equal("argon2id", kdf.GetProperty("alg").GetString());
        Assert.Equal(128 * 1024, kdf.GetProperty("m").GetInt32());
        Assert.Equal(3, kdf.GetProperty("t").GetInt32());
        Assert.Equal(4, kdf.GetProperty("p").GetInt32());
        using var keys = SyncKeyVault.UnlockWithPassphrase(created.KeyringData, Pass);
        Assert.Equal(created.Keys.Current, keys.Current);
    }

    [Fact]
    public void A_keyring_from_helm_0_5_opens_and_upgrades_to_argon2id_without_changing_the_key()
    {
        var (legacy, master, recovery) = LegacyKeyring("old but long passphrase");

        Assert.True(SyncKeyVault.NeedsUpgrade(legacy));
        using (var opened = SyncKeyVault.UnlockWithPassphrase(legacy, "old but long passphrase"))
            Assert.Equal(master, opened.Current);
        using (var opened = SyncKeyVault.UnlockWithRecoveryKey(legacy, recovery))
            Assert.Equal(master, opened.Current);
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.Upgrade(legacy, "wrong passphrase!!", Fast));

        var upgraded = SyncKeyVault.Upgrade(legacy, "old but long passphrase", Fast);

        Assert.False(SyncKeyVault.NeedsUpgrade(upgraded));
        Assert.Contains("argon2id", upgraded);
        using (var opened = SyncKeyVault.UnlockWithPassphrase(upgraded, "old but long passphrase"))
            Assert.Equal(master, opened.Current);
        using (var opened = SyncKeyVault.UnlockWithRecoveryKey(upgraded, recovery))
            Assert.Equal(master, opened.Current);
    }

    [Fact]
    public void Wrong_secrets_are_refused_with_a_clear_message()
    {
        var created = SyncKeyVault.Create(Pass, Fast);

        var wrong = Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithPassphrase(created.KeyringData, Pass + "!"));
        Assert.Contains("passphrase", wrong.Message);
        var other = SyncKeyVault.Create("another quite long passphrase", Fast);
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithRecoveryKey(created.KeyringData, other.RecoveryKey));
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithRecoveryKey(created.KeyringData, "HELM-1234"));
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithPassphrase("{not json", "x"));
    }

    [Fact]
    public void Rotation_makes_a_new_key_keeps_old_ones_readable_and_replaces_the_recovery_key()
    {
        var created = SyncKeyVault.Create(Pass, Fast);

        var rotated = SyncKeyVault.Rotate(created.KeyringData, Pass, Fast);

        Assert.Equal(2, rotated.Keys.CurrentEpoch);
        Assert.NotEqual(created.Keys.Current, rotated.Keys.Current);
        Assert.True(rotated.Keys.TryGet(1, out var old) && old.SequenceEqual(created.Keys.Current));
        using (var byPass = SyncKeyVault.UnlockWithPassphrase(rotated.KeyringData, Pass))
        {
            Assert.Equal(2, byPass.CurrentEpoch);
            Assert.Equal(rotated.Keys.Current, byPass.Current);
            Assert.True(byPass.TryGet(1, out var kept) && kept.SequenceEqual(created.Keys.Current));
        }
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithRecoveryKey(rotated.KeyringData, created.RecoveryKey));
        using (var byNewRecovery = SyncKeyVault.UnlockWithRecoveryKey(rotated.KeyringData, rotated.RecoveryKey))
            Assert.Equal(rotated.Keys.Current, byNewRecovery.Current);
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.Rotate(created.KeyringData, "not the passphrase", Fast));

        var again = SyncKeyVault.Rotate(rotated.KeyringData, Pass, Fast);
        using var third = SyncKeyVault.UnlockWithPassphrase(again.KeyringData, Pass);
        Assert.Equal([1, 2, 3], third.Keys.Keys.Order());
    }

    [Fact]
    public void Changing_the_passphrase_keeps_the_keys_and_the_recovery_key()
    {
        var created = SyncKeyVault.Create("first long passphrase ok", Fast);
        var changed = SyncKeyVault.ChangePassphrase(created.KeyringData, created.Keys, "second long passphrase ok");

        using (var opened = SyncKeyVault.UnlockWithPassphrase(changed, "second long passphrase ok"))
            Assert.Equal(created.Keys.Current, opened.Current);
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithPassphrase(changed, "first long passphrase ok"));
        using (var opened = SyncKeyVault.UnlockWithRecoveryKey(changed, created.RecoveryKey))
            Assert.Equal(created.Keys.Current, opened.Current);
    }

    [Theory]
    [InlineData("short", PassphraseStrength.TooShort)]
    [InlineData("aaaaaaaaaaaaaaaaaaaa", PassphraseStrength.Weak)]
    [InlineData("abcdefghijklmnopqrst", PassphraseStrength.Weak)]
    [InlineData("12345678901234567890", PassphraseStrength.Weak)]
    [InlineData("correct horse battery staple", PassphraseStrength.Strong)]
    [InlineData("Tr0ub4dor&3xk9Lp", PassphraseStrength.Strong)]
    public void Strength_estimate_rejects_short_and_patterned_passphrases(string passphrase, PassphraseStrength expected)
    {
        Assert.Equal(expected, SyncKeyVault.EstimateStrength(passphrase));
    }

    [Fact]
    public void Weak_or_short_new_passphrases_are_refused()
    {
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.Create("short", Fast));
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.Create("aaaaaaaaaaaaaaaaaaaa", Fast));
        var created = SyncKeyVault.Create(Pass, Fast);
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.ChangePassphrase(created.KeyringData, created.Keys, "12345678901234567890"));
    }

    [Fact]
    public void Recovery_keys_tolerate_case_spaces_and_look_alike_letters()
    {
        var created = SyncKeyVault.Create(Pass, Fast);
        Assert.Matches("^HELM-([0-9A-HJKMNP-TV-Z]{4}-){12}[0-9A-HJKMNP-TV-Z]{4}$", created.RecoveryKey);

        var sloppy = created.RecoveryKey.ToLowerInvariant().Replace("-", " ").Replace('0', 'o').Replace('1', 'l');
        using var opened = SyncKeyVault.UnlockWithRecoveryKey(created.KeyringData, sloppy);
        Assert.Equal(created.Keys.Current, opened.Current);
    }

    [Fact]
    public void The_keyring_hides_every_secret()
    {
        var created = SyncKeyVault.Create(Pass, Fast);
        Assert.DoesNotContain("horse", created.KeyringData);
        Assert.DoesNotContain(Convert.ToBase64String(created.Keys.Current), created.KeyringData);
        Assert.DoesNotContain(created.RecoveryKey, created.KeyringData);
    }

    [Fact]
    public void Key_sets_round_trip_and_read_the_bare_key_of_helm_0_5()
    {
        var bare = SyncKeyring.CreateMasterKey();
        using var legacy = SyncKeySet.TryParse(bare)!;
        Assert.Equal(1, legacy.CurrentEpoch);
        Assert.Equal(bare, legacy.Current);

        var two = new SyncKeySet(2, new Dictionary<int, byte[]> { [1] = bare, [2] = SyncKeyring.CreateMasterKey() });
        using var back = SyncKeySet.TryParse(two.Serialize())!;
        Assert.Equal(2, back.CurrentEpoch);
        Assert.Equal(two.Current, back.Current);
        Assert.True(back.TryGet(1, out var one) && one.SequenceEqual(bare));
        Assert.Null(SyncKeySet.TryParse("garbage"u8.ToArray()));
    }

    [Fact]
    public void Recovery_key_format_round_trips()
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
        Assert.Equal(secret, SyncKeyVault.ParseRecoveryKey(SyncKeyVault.FormatRecoveryKey(secret)));
    }

    /// <summary>A keyring exactly as Helm 0.5.0 wrote it (v1, PBKDF2), built independently of the current code.</summary>
    private static (string Keyring, byte[] Master, string Recovery) LegacyKeyring(string passphrase)
    {
        var master = RandomNumberGenerator.GetBytes(32);
        var recoverySecret = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        const int iterations = 100_000;
        var passKek = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);
        var recKek = HKDF.DeriveKey(HashAlgorithmName.SHA256, recoverySecret, 32, salt: [], info: Encoding.UTF8.GetBytes("helm-sync/v1/recovery"));
        var json = JsonSerializer.Serialize(new
        {
            v = 1,
            kdf = new { alg = "pbkdf2-sha256", iter = iterations, salt },
            pass = Wrap(passKek, master, "helm-sync/v1/keyring|passphrase"),
            rec = Wrap(recKek, master, "helm-sync/v1/keyring|recovery"),
        });
        return (json, master, SyncKeyVault.FormatRecoveryKey(recoverySecret));
    }

    private static byte[] Wrap(byte[] kek, byte[] master, string aad)
    {
        var output = new byte[12 + 16 + 32];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var gcm = new AesGcm(kek, 16);
        gcm.Encrypt(output.AsSpan(0, 12), master, output.AsSpan(28), output.AsSpan(12, 16), Encoding.UTF8.GetBytes(aad));
        return output;
    }
}
