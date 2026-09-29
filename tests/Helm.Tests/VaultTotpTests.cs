using System.Text;
using Helm.Modules.Vault.Items;

namespace Helm.Tests;

/// <summary>Two-factor codes (RFC 6238), checked against the test vectors of the RFC itself.</summary>
public sealed class VaultTotpTests
{
    private static readonly byte[] Sha1Key = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly byte[] Sha256Key = Encoding.ASCII.GetBytes("12345678901234567890123456789012");
    private static readonly byte[] Sha512Key = Encoding.ASCII.GetBytes("1234567890123456789012345678901234567890123456789012345678901234");

    [Theory]
    [InlineData(59L, "94287082", "46119246", "90693936")]
    [InlineData(1111111109L, "07081804", "68084774", "25091201")]
    [InlineData(1111111111L, "14050471", "67062674", "99943326")]
    [InlineData(1234567890L, "89005924", "91819424", "93441116")]
    [InlineData(2000000000L, "69279037", "90698825", "38618901")]
    [InlineData(20000000000L, "65353130", "77737706", "47863826")]
    public void Codes_match_the_rfc_6238_vectors(long unixSeconds, string sha1, string sha256, string sha512)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(sha1, new Totp(Sha1Key, Digits: 8).Code(at));
        Assert.Equal(sha256, new Totp(Sha256Key, Digits: 8, Algorithm: TotpAlgorithm.Sha256).Code(at));
        Assert.Equal(sha512, new Totp(Sha512Key, Digits: 8, Algorithm: TotpAlgorithm.Sha512).Code(at));
    }

    [Fact]
    public void A_key_as_sites_print_it_is_read_whatever_its_spacing_and_case()
    {
        Assert.True(Totp.TryParse("gezd gnbv gy3t qojq gezd gnbv gy3t qojq", out var totp, out _));
        Assert.Equal(Sha1Key, totp!.Secret);
        Assert.Equal((6, 30, TotpAlgorithm.Sha1), (totp.Digits, totp.Period, totp.Algorithm));
        Assert.Equal("287082", totp.Code(DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal(1, totp.SecondsLeft(DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal(30, totp.SecondsLeft(DateTimeOffset.FromUnixTimeSeconds(60)));
        Assert.Equal("287 082", Totp.Group("287082"));
        Assert.Equal("9428 7082", Totp.Group("94287082"));
    }

    [Fact]
    public void An_otpauth_link_brings_its_issuer_account_and_settings()
    {
        Assert.True(Totp.TryParse("otpauth://totp/Example:alice%40google.com?secret=JBSWY3DPEHPK3PXP&issuer=Example", out var totp, out _));
        Assert.Equal(("Example", "alice@google.com"), (totp!.Issuer, totp.Account));

        Assert.True(Totp.TryParse("otpauth://totp/ACME?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&digits=8&period=60&algorithm=SHA256", out var custom, out _));
        Assert.Equal((8, 60, TotpAlgorithm.Sha256, "ACME"), (custom!.Digits, custom.Period, custom.Algorithm, custom.Account));

        var back = Totp.Parse(Totp.ToUri(custom, "ACME"))!;
        Assert.Equal(custom.Secret, back.Secret);
        Assert.Equal((8, 60, TotpAlgorithm.Sha256), (back.Digits, back.Period, back.Algorithm));
    }

    [Theory]
    [InlineData("", "Paste the key")]
    [InlineData("not a key!", "not a valid key")]
    [InlineData("ABCD", "not a valid key")]
    [InlineData("otpauth://hotp/X?secret=JBSWY3DPEHPK3PXP&counter=1", "HOTP")]
    [InlineData("otpauth://totp/X?digits=6", "no valid secret")]
    [InlineData("otpauth://totp/X?secret=JBSWY3DPEHPK3PXP&digits=12", "cannot make")]
    [InlineData("otpauth://totp/X?secret=JBSWY3DPEHPK3PXP&algorithm=MD5", "unknown algorithm")]
    public void Anything_else_is_refused_with_a_reason(string value, string reason)
    {
        Assert.False(Totp.TryParse(value, out var totp, out var error));
        Assert.Null(totp);
        Assert.Contains(reason, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_secret_never_shows_in_logs()
    {
        var totp = Totp.Parse("JBSWY3DPEHPK3PXP")!;
        Assert.DoesNotContain("JBSW", totp.ToString());
        Assert.DoesNotContain(Convert.ToHexString(totp.Secret), totp.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_item_finds_its_first_valid_two_factor_field()
    {
        var item = VaultItem.New(VaultItemKind.Login, "GitHub") with
        {
            Fields = [new("Username", "me", VaultFieldKind.Username), new("Code", "nope", VaultFieldKind.Totp), new("Code", "JBSWY3DPEHPK3PXP", VaultFieldKind.Totp)],
        };
        Assert.NotNull(item.Totp);
        Assert.Null(item.Password);
        var token = VaultItem.New(VaultItemKind.Token, "API") with
        {
            Fields = [new("2FA", "JBSWY3DPEHPK3PXP", VaultFieldKind.Totp), new("Token", "tok_123", VaultFieldKind.Secret)],
        };
        // A token's secret is its token, never its two-factor key.
        Assert.Equal("tok_123", token.PrimarySecret);
    }
}
