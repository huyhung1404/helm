using Helm.Typing;
using Helm.Modules.Keyboard;

namespace Helm.Tests;

public sealed class ModifierTapTests
{
    private const int LShift = 0xA0, LCtrl = 0xA2, RCtrl = 0xA3, T = 'T';

    private static List<bool> Feed(ModifierTap tap, params (int Key, bool Down)[] keys) => keys.Select(k => tap.OnKey(k.Key, k.Down)).ToList();

    [Fact]
    public void Ctrl_and_Shift_pressed_and_let_go_fire_once()
    {
        var fired = Feed(new ModifierTap(LCtrl, LShift), (LCtrl, true), (LShift, true), (LShift, false), (LCtrl, false));
        Assert.Equal([false, false, true, false], fired);
    }

    [Fact]
    public void The_order_of_the_keys_does_not_matter()
    {
        var fired = Feed(new ModifierTap(LCtrl, LShift), (LShift, true), (LCtrl, true), (LCtrl, false), (LShift, false));
        Assert.Equal(1, fired.Count(f => f));
    }

    [Fact]
    public void Held_keys_repeating_still_fire_once()
    {
        var fired = Feed(new ModifierTap(LCtrl, LShift), (LCtrl, true), (LShift, true), (LShift, true), (LShift, true), (LCtrl, false), (LShift, false));
        Assert.Equal(1, fired.Count(f => f));
    }

    [Fact]
    public void Another_key_in_between_makes_it_that_keys_shortcut()
    {
        var tap = new ModifierTap(LCtrl, LShift);
        Assert.DoesNotContain(true, Feed(tap, (LCtrl, true), (LShift, true), (T, true), (T, false), (LShift, false), (LCtrl, false)));
        // The next one works again.
        Assert.Contains(true, Feed(tap, (LCtrl, true), (LShift, true), (LShift, false), (LCtrl, false)));
    }

    [Fact]
    public void One_key_alone_is_not_the_shortcut()
    {
        Assert.DoesNotContain(true, Feed(new ModifierTap(LCtrl, LShift), (LCtrl, true), (LCtrl, false), (LShift, true), (LShift, false)));
    }

    [Fact]
    public void A_single_key_shortcut_fires_when_let_go()
    {
        var tap = new ModifierTap(RCtrl);
        Assert.Equal([false, true], Feed(tap, (RCtrl, true), (RCtrl, false)));
        Assert.DoesNotContain(true, Feed(tap, (RCtrl, true), ('C', true), ('C', false), (RCtrl, false)));
    }
}

public sealed class KeyboardModesTests
{
    [Theory]
    [InlineData(KeyboardMode.English, true, KeyboardMode.Vietnamese)]
    [InlineData(KeyboardMode.Vietnamese, true, KeyboardMode.Chinese)]
    [InlineData(KeyboardMode.Chinese, true, KeyboardMode.English)]
    [InlineData(KeyboardMode.Vietnamese, false, KeyboardMode.English)]
    [InlineData(KeyboardMode.Chinese, false, KeyboardMode.English)]
    public void The_shortcut_goes_round_English_Vietnamese_Chinese(KeyboardMode from, bool withChinese, KeyboardMode next) =>
        Assert.Equal(next, KeyboardModes.Next(from, withChinese));

    [Theory]
    [InlineData(0x0409, false, KeyboardMode.English)]
    [InlineData(0x0409, true, KeyboardMode.Vietnamese)]
    [InlineData(0x0804, false, KeyboardMode.Chinese)]
    [InlineData(0x0804, true, KeyboardMode.Chinese)]
    [InlineData(0x0404, false, KeyboardMode.Chinese)]
    public void A_window_types_in_the_mode_of_its_keyboard_and_the_switch(int language, bool vietnamese, KeyboardMode mode) =>
        Assert.Equal(mode, KeyboardModes.From(language, vietnamese));

    [Fact]
    public void The_input_method_is_written_as_Windows_keyboard_list_has_it() =>
        Assert.Equal("0409:{6710956B-FB8A-44C4-BEDF-ED7B2BC5961E}{9294B09C-ED79-41B8-98B6-A3DDB5151A74}", KeyboardIds.InputMethod);
}

public sealed class KeyboardAppsTests
{
    private readonly MemorySynced<KeyboardAppMode> _records = new();
    private readonly KeyboardApps _apps;

    public KeyboardAppsTests() => _apps = new KeyboardApps(_records);

    [Fact]
    public void An_app_never_seen_types_English() => Assert.Equal(KeyboardMode.English, _apps.ModeOf("notepad.exe"));

    [Fact]
    public void An_app_gets_back_the_keyboard_it_was_left_with_whatever_the_case_of_its_name()
    {
        _apps.Remember("Code.exe", KeyboardMode.Vietnamese);
        Assert.Equal(KeyboardMode.Vietnamese, _apps.ModeOf("code.exe"));
        Assert.Equal("Code.exe", Assert.Single(_apps.All()).App);
    }

    [Fact]
    public void Back_to_English_the_app_drops_off_the_list()
    {
        _apps.Remember("chrome.exe", KeyboardMode.Chinese);
        _apps.Remember("chrome.exe", KeyboardMode.English);
        Assert.Empty(_apps.All());
        Assert.Equal(KeyboardMode.English, _apps.ModeOf("chrome.exe"));
    }

    [Fact]
    public void The_same_keyboard_again_writes_nothing()
    {
        _apps.Remember("slack.exe", KeyboardMode.Vietnamese);
        var changes = 0;
        _apps.Changed += (_, _) => changes++;
        _apps.Remember("slack.exe", KeyboardMode.Vietnamese);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_site_keeps_its_own_keyboard_apart_from_the_browser_and_other_sites()
    {
        _apps.Remember(KeyboardApps.SiteKeyOf("bilibili.com"), KeyboardMode.Chinese);
        _apps.Remember(KeyboardApps.SiteKeyOf("facebook.com"), KeyboardMode.Vietnamese);
        Assert.Equal(KeyboardMode.Chinese, _apps.ModeOf(KeyboardApps.SiteKeyOf("bilibili.com")));
        Assert.Equal(KeyboardMode.Vietnamese, _apps.ModeOf(KeyboardApps.SiteKeyOf("facebook.com")));
        // The browser app itself, never remembered, is untouched.
        Assert.Equal(KeyboardMode.English, _apps.ModeOf("msedge.exe"));
    }

    [Fact]
    public void Knows_tells_a_seen_site_from_a_new_one()
    {
        Assert.False(_apps.Knows("bilibili.com"));
        _apps.Remember(KeyboardApps.SiteKeyOf("bilibili.com"), KeyboardMode.Chinese);
        Assert.True(_apps.Knows("bilibili.com"));
        Assert.True(_apps.Knows("BILIBILI.COM"));
        Assert.False(_apps.Knows(null));
    }
}

public sealed class BrowserSiteDomainTests
{
    [Theory]
    [InlineData("https://www.facebook.com/me", "facebook.com")]
    [InlineData("https://m.facebook.com/x", "facebook.com")]
    [InlineData("https://www.bilibili.com/video/BV1", "bilibili.com")]
    [InlineData("bilibili.com", "bilibili.com")]
    [InlineData("https://accounts.google.com/signin", "google.com")]
    [InlineData("https://news.bbc.co.uk/sport", "bbc.co.uk")]
    [InlineData("https://shop.example.com.vn/cart", "example.com.vn")]
    [InlineData("https://example.com.vn/cart", "example.com.vn")]
    [InlineData("http://sub.deep.example.com/", "example.com")]
    public void The_registrable_domain_is_what_a_site_is_remembered_by(string url, string domain) =>
        Assert.Equal(domain, Helm.Core.Desktop.BrowserSites.RegistrableDomain(url));

    [Theory]
    [InlineData("edge://settings")]
    [InlineData("chrome://newtab")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/notes.html")]
    [InlineData("http://localhost:3000/")]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_page_with_no_site_of_its_own_gives_nothing(string url) =>
        Assert.Null(Helm.Core.Desktop.BrowserSites.RegistrableDomain(url));
}
