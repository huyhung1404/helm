using Helm.Modules.Vocab;

namespace Helm.Tests;

/// <summary>The phone's pop-up card and bubble: when they come, and when they stay away.</summary>
public sealed class VocabOverlayTests
{
    private static readonly VocabOverlayState Ready = new()
    {
        ForegroundApp = "com.browser",
        OwnApp = "com.huyhung1404.helm",
        BlockedApps = ["com.game", "com.video"],
    };

    [Fact]
    public void The_next_card_comes_within_the_chosen_range()
    {
        var random = new Random(3);
        for (var i = 0; i < 200; i++)
        {
            var delay = VocabPopupSchedule.NextDelay(20 * 60, 60 * 60, random);
            Assert.InRange(delay, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(60));
        }
        // Ends the wrong way round, or too short, are put right.
        Assert.InRange(VocabPopupSchedule.NextDelay(90, 30, random), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90));
        Assert.Equal(TimeSpan.FromSeconds(VocabSettingsViewModel.MinPopupSeconds), VocabPopupSchedule.NextDelay(1, 1, random));
    }

    [Fact]
    public void A_card_that_could_not_show_tries_again_soon_but_never_later_than_the_range()
    {
        Assert.Equal(VocabPopupSchedule.Retry, VocabPopupSchedule.RetryAfter(60 * 60));
        Assert.Equal(TimeSpan.FromSeconds(90), VocabPopupSchedule.RetryAfter(90));
        Assert.Equal(TimeSpan.FromSeconds(VocabSettingsViewModel.MinPopupSeconds), VocabPopupSchedule.RetryAfter(0));
    }

    [Fact]
    public void The_card_shows_over_an_ordinary_app_held_upright()
    {
        Assert.Equal(VocabOverlayVerdict.Show, VocabOverlayRules.Card(Ready, popupEnabled: true));
        // Without usage access the app in front is unknown: only being sideways or in Helm keeps it away then.
        Assert.Equal(VocabOverlayVerdict.Show, VocabOverlayRules.Card(Ready with { ForegroundApp = null }, popupEnabled: true));
    }

    [Theory]
    [InlineData("landscape", VocabOverlayVerdict.Landscape)]
    [InlineData("blocked", VocabOverlayVerdict.BlockedApp)]
    [InlineData("helm", VocabOverlayVerdict.InHelm)]
    [InlineData("locked", VocabOverlayVerdict.NotInUse)]
    [InlineData("no-permission", VocabOverlayVerdict.NoPermission)]
    [InlineData("module-off", VocabOverlayVerdict.Off)]
    [InlineData("no-word", VocabOverlayVerdict.NoWord)]
    public void The_card_stays_away(string why, VocabOverlayVerdict expected)
    {
        var state = why switch
        {
            "landscape" => Ready with { Landscape = true },
            "blocked" => Ready with { ForegroundApp = "com.game" },
            "helm" => Ready with { ForegroundApp = "com.huyhung1404.helm" },
            "locked" => Ready with { InUse = false },
            "no-permission" => Ready with { CanDraw = false },
            "module-off" => Ready with { ModuleOn = false },
            _ => Ready with { HasWord = false },
        };

        Assert.Equal(expected, VocabOverlayRules.Card(state, popupEnabled: true));
    }

    [Fact]
    public void A_card_turned_off_never_shows()
    {
        Assert.Equal(VocabOverlayVerdict.Off, VocabOverlayRules.Card(Ready, popupEnabled: false));
    }

    [Fact]
    public void The_bubble_hides_sideways_in_blocked_apps_and_in_helm_and_after_being_put_away()
    {
        Assert.Equal(VocabOverlayVerdict.Show, VocabOverlayRules.Bubble(Ready, bubbleEnabled: true, dismissedUntilUnlock: false));
        // It needs no word: tapping it says when none is ready.
        Assert.Equal(VocabOverlayVerdict.Show, VocabOverlayRules.Bubble(Ready with { HasWord = false }, true, false));

        Assert.Equal(VocabOverlayVerdict.Off, VocabOverlayRules.Bubble(Ready, bubbleEnabled: false, dismissedUntilUnlock: false));
        Assert.Equal(VocabOverlayVerdict.Landscape, VocabOverlayRules.Bubble(Ready with { Landscape = true }, true, false));
        Assert.Equal(VocabOverlayVerdict.BlockedApp, VocabOverlayRules.Bubble(Ready with { ForegroundApp = "com.video" }, true, false));
        Assert.Equal(VocabOverlayVerdict.InHelm, VocabOverlayRules.Bubble(Ready with { ForegroundApp = "com.huyhung1404.helm" }, true, false));
        Assert.Equal(VocabOverlayVerdict.DismissedUntilUnlock, VocabOverlayRules.Bubble(Ready, true, dismissedUntilUnlock: true));
    }

    [Fact]
    public void Blocked_apps_match_by_exact_package()
    {
        Assert.True(VocabOverlayRules.IsBlocked("com.game", ["com.game"]));
        Assert.False(VocabOverlayRules.IsBlocked("com.game.lite", ["com.game"]));
        Assert.False(VocabOverlayRules.IsBlocked(null, ["com.game"]));
    }
}
