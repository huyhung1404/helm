using System.Text;
using Helm.Core.Sync;

namespace Helm.Tests;

public sealed class SyncKeyVaultTests
{
    private const int FastIterations = 100_000;

    [Fact]
    public void Passphrase_and_recovery_key_both_open_the_same_master_key()
    {
        var keys = SyncKeyVault.Create("correct horse battery", FastIterations);

        Assert.Equal(keys.MasterKey, SyncKeyVault.UnlockWithPassphrase(keys.KeyringData, "correct horse battery"));
        Assert.Equal(keys.MasterKey, SyncKeyVault.UnlockWithRecoveryKey(keys.KeyringData, keys.RecoveryKey));
    }

    [Fact]
    public void Default_cost_is_600k_pbkdf2_iterations()
    {
        var keys = SyncKeyVault.Create("correct horse battery");
        Assert.Contains("\"iter\":600000", keys.KeyringData);
        Assert.Equal(keys.MasterKey, SyncKeyVault.UnlockWithPassphrase(keys.KeyringData, "correct horse battery"));
    }

    [Fact]
    public void Wrong_secrets_are_refused_with_a_clear_message()
    {
        var keys = SyncKeyVault.Create("correct horse battery", FastIterations);

        var wrong = Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithPassphrase(keys.KeyringData, "correct horse batterY"));
        Assert.Contains("passphrase", wrong.Message);
        var other = SyncKeyVault.Create("another passphrase!", FastIterations);
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithRecoveryKey(keys.KeyringData, other.RecoveryKey));
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithRecoveryKey(keys.KeyringData, "HELM-1234"));
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithPassphrase("{not json", "x"));
    }

    [Fact]
    public void Recovery_keys_tolerate_case_spaces_and_look_alike_letters()
    {
        var keys = SyncKeyVault.Create("correct horse battery", FastIterations);
        Assert.Matches("^HELM-([0-9A-HJKMNP-TV-Z]{4}-){12}[0-9A-HJKMNP-TV-Z]{4}$", keys.RecoveryKey);

        var sloppy = keys.RecoveryKey.ToLowerInvariant().Replace("-", " ").Replace('0', 'o').Replace('1', 'l');
        Assert.Equal(keys.MasterKey, SyncKeyVault.UnlockWithRecoveryKey(keys.KeyringData, sloppy));
    }

    [Fact]
    public void Changing_the_passphrase_keeps_the_master_and_the_recovery_key()
    {
        var keys = SyncKeyVault.Create("first passphrase", FastIterations);
        var changed = SyncKeyVault.ChangePassphrase(keys.KeyringData, keys.MasterKey, "second passphrase");

        Assert.Equal(keys.MasterKey, SyncKeyVault.UnlockWithPassphrase(changed, "second passphrase"));
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.UnlockWithPassphrase(changed, "first passphrase"));
        Assert.Equal(keys.MasterKey, SyncKeyVault.UnlockWithRecoveryKey(changed, keys.RecoveryKey));
    }

    [Fact]
    public void Short_passphrases_are_rejected_and_the_keyring_hides_every_secret()
    {
        Assert.Throws<SyncKeyException>(() => SyncKeyVault.Create("short", FastIterations));

        var keys = SyncKeyVault.Create("correct horse battery", FastIterations);
        Assert.DoesNotContain("correct horse", keys.KeyringData);
        Assert.DoesNotContain(Convert.ToBase64String(keys.MasterKey), keys.KeyringData);
        Assert.DoesNotContain(keys.RecoveryKey, keys.KeyringData);
        Assert.DoesNotContain(Encoding.UTF8.GetString(keys.MasterKey), keys.KeyringData);
    }

    [Fact]
    public void Recovery_key_format_round_trips()
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
        Assert.Equal(secret, SyncKeyVault.ParseRecoveryKey(SyncKeyVault.FormatRecoveryKey(secret)));
    }
}
