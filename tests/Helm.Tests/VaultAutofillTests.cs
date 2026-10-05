using Helm.Modules.Vault.Items;

namespace Helm.Tests;

/// <summary>Which logins autofill and auto-type offer, and how login fields are recognised.</summary>
public sealed class VaultAutofillTests
{
    private static VaultEntry Login(string title, string password, params string[] urls) => new(
        title, VaultItem.New(VaultItemKind.Login, title) with
        {
            Fields = [new("Username", title.ToLowerInvariant() + "@me", VaultFieldKind.Username), new("Password", password, VaultFieldKind.Password),
                .. urls.Select(u => new VaultField("Website", u, VaultFieldKind.Url))],
        }, false, null, DateTimeOffset.UnixEpoch, [], title);

    private static readonly VaultEntry[] Entries =
    [
        Login("Google", "g", "https://accounts.google.com"),
        Login("Gmail", "gm", "google.com"),
        Login("PayPal", "pp", "https://www.paypal.com/signin"),
        Login("Facebook", "fb", "https://facebook.com", "androidapp://com.facebook.katana"),
        Login("Slack", "sl", "app://slack"),
        Login("No password", "", "https://nopass.example"),
        Login("Bank", "bk", "androidapp://vn.bank.app#aa11bb22"),
    ];

    [Fact]
    public void An_app_remembered_with_its_certificate_matches_only_that_signature()
    {
        var real = Assert.Single(VaultAutofill.Match(Entries, new AutofillTarget(null, "vn.bank.app", AppCert: "AA11BB22")));
        Assert.Equal("Bank", real.Title);
        Assert.True(real.Verified);
        // A fake app installed from a file can take the bank's package name, never its signing key.
        Assert.Empty(VaultAutofill.Match(Entries, new AutofillTarget(null, "vn.bank.app", AppCert: "ffff0000")));
        // A certificate that could not be read is not trusted either.
        Assert.Empty(VaultAutofill.Match(Entries, new AutofillTarget(null, "vn.bank.app")));
    }

    [Fact]
    public void An_app_remembered_without_a_certificate_is_only_offered_in_the_picker()
    {
        var facebook = Assert.Single(VaultAutofill.Match(Entries, new AutofillTarget(null, "com.facebook.katana", AppCert: "aa")));
        Assert.False(facebook.Verified);
        // Sites and Windows apps are unaffected.
        Assert.True(Assert.Single(VaultAutofill.Match(Entries, new AutofillTarget("www.paypal.com"))).Verified);
        Assert.True(Assert.Single(VaultAutofill.Match(Entries, new AutofillTarget(null, "slack"))).Verified);
    }

    [Fact]
    public void App_urls_carry_the_certificate_on_android_only()
    {
        Assert.Equal("androidapp://vn.bank.app#aa11", VaultAutofill.AppUrl(VaultAutofill.AndroidAppScheme, "Vn.Bank.App", "AA11"));
        Assert.Equal("androidapp://vn.bank.app", VaultAutofill.AppUrl(VaultAutofill.AndroidAppScheme, "vn.bank.app"));
        Assert.Equal("app://slack", VaultAutofill.AppUrl(VaultAutofill.WindowsAppScheme, "Slack", "aa11"));
    }

    [Fact]
    public void Sites_match_exactly_or_by_parent_domain_and_never_by_look_alike()
    {
        Assert.Equal(["Google", "Gmail"], VaultAutofill.Match(Entries, new AutofillTarget("accounts.google.com")).Select(m => m.Title));
        Assert.Equal(["Gmail", "Google"], VaultAutofill.Match(Entries, new AutofillTarget("google.com")).Select(m => m.Title));
        Assert.Equal(["PayPal"], VaultAutofill.Match(Entries, new AutofillTarget("www.PayPal.com")).Select(m => m.Title));
        Assert.Empty(VaultAutofill.Match(Entries, new AutofillTarget("paypa1.com")));
        Assert.Empty(VaultAutofill.Match(Entries, new AutofillTarget("paypal.com.evil.io")));
        Assert.Empty(VaultAutofill.Match(Entries, new AutofillTarget("notgoogle.com")));
        Assert.Empty(VaultAutofill.Match(Entries, new AutofillTarget("nopass.example")));
    }

    [Fact]
    public void Apps_match_only_what_was_remembered_for_them()
    {
        Assert.Equal(["Facebook"], VaultAutofill.Match(Entries, new AutofillTarget(null, "com.facebook.katana")).Select(m => m.Title));
        Assert.Equal(["Slack"], VaultAutofill.Match(Entries, new AutofillTarget(null, "Slack")).Select(m => m.Title));
        Assert.Empty(VaultAutofill.Match(Entries, new AutofillTarget(null, "com.facebook.katana.fake")));
    }

    [Fact]
    public void A_window_title_only_suggests()
    {
        var suggested = VaultAutofill.Suggest(Entries, "Log in to Facebook | Facebook - Google Chrome").Select(m => m.Title).ToList();
        Assert.Contains("Facebook", suggested);
        Assert.Contains("Google", suggested); // "google" is in "Google Chrome": only a suggestion, the user picks
        Assert.Empty(VaultAutofill.Suggest(Entries, ""));
    }

    [Fact]
    public void Hosts_are_read_from_what_people_save()
    {
        Assert.Equal("example.com", VaultAutofill.HostOf("https://www.Example.com:443/login?x=1"));
        Assert.Equal("example.com", VaultAutofill.HostOf("example.com"));
        Assert.Null(VaultAutofill.HostOf("androidapp://com.example"));
        Assert.Null(VaultAutofill.HostOf("not a url"));
        Assert.Null(VaultAutofill.HostOf("ftp://example.com"));
    }

    [Theory]
    [InlineData(new[] { "password" }, false, null, null, null, null, null, AutofillFieldKind.Password)]
    [InlineData(new[] { "emailAddress" }, false, null, null, null, null, null, AutofillFieldKind.Username)]
    [InlineData(new[] { "smsOTPCode" }, false, null, null, null, null, null, AutofillFieldKind.OneTimeCode)]
    [InlineData(null, true, null, null, null, "edit_secret", null, AutofillFieldKind.Password)]
    [InlineData(null, false, "password", "pw", null, null, null, AutofillFieldKind.Password)]
    [InlineData(null, false, "text", "login_email", null, null, null, AutofillFieldKind.Username)]
    [InlineData(null, false, "email", null, null, null, null, AutofillFieldKind.Username)]
    [InlineData(null, false, "text", null, "one-time-code", null, null, AutofillFieldKind.OneTimeCode)]
    [InlineData(null, false, "text", null, "username", null, null, AutofillFieldKind.Username)]
    [InlineData(null, false, "text", null, null, null, "Tài khoản", AutofillFieldKind.Username)]
    [InlineData(null, false, "text", null, null, null, "Mật khẩu", AutofillFieldKind.Password)]
    [InlineData(null, false, "checkbox", "remember_password", null, null, null, AutofillFieldKind.None)]
    [InlineData(null, false, "search", "q", null, null, null, AutofillFieldKind.None)]
    [InlineData(null, false, "text", "first_name", null, null, null, AutofillFieldKind.None)]
    public void Login_fields_are_recognised(string[]? hints, bool hides, string? type, string? name, string? autocomplete, string? id, string? hint, AutofillFieldKind expected) =>
        Assert.Equal(expected, AutofillFieldClassifier.Classify(hints, hides, type, name, autocomplete, id, hint));
}
