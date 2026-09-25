using Helm.Core.Services;
using Helm.Core.Settings;

namespace Helm.Tests;

public class UpdatePolicyTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    [InlineData("1.0.0-preview.1", "1.0.0", -1)]
    [InlineData("1.0.0-preview.2", "1.0.0-preview.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0.0-preview.1", "1.0.0-preview", 1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    public void Semantic_version_precedence(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(UpdatePolicy.Compare(a, b)));
        Assert.Equal(-expected, Math.Sign(UpdatePolicy.Compare(b, a)));
    }

    [Fact]
    public void Is_newer_only_for_strictly_greater_versions()
    {
        Assert.True(UpdatePolicy.IsNewer("0.3.1", "0.3.0"));
        Assert.False(UpdatePolicy.IsNewer("0.3.0", "0.3.0"));
        Assert.False(UpdatePolicy.IsNewer("0.3.0-preview.1", "0.3.0"));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("refs/tags/v0.4.0-preview.1", "0.4.0-preview.1")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("release-1", null)]
    [InlineData("v1.2", null)]
    [InlineData("", null)]
    public void Version_from_tag(string tag, string? expected)
    {
        Assert.Equal(expected, UpdatePolicy.VersionFromTag(tag));
    }

    [Theory]
    [InlineData("1.2.3", UpdatePolicy.StableChannel)]
    [InlineData("0.4.0-preview.1", UpdatePolicy.PreviewChannel)]
    [InlineData("1.0.0-rc.1", UpdatePolicy.PreviewChannel)]
    public void Channel_follows_the_pre_release_suffix(string version, string channel)
    {
        Assert.Equal(channel, UpdatePolicy.ChannelForVersion(version));
    }

    [Fact]
    public void Channel_selection_maps_to_velopack_channels_and_prereleases()
    {
        Assert.Equal("stable", UpdatePolicy.ChannelName(UpdateChannel.Stable));
        Assert.Equal("preview", UpdatePolicy.ChannelName(UpdateChannel.Preview));
        Assert.False(UpdatePolicy.IncludePrereleases(UpdateChannel.Stable));
        Assert.True(UpdatePolicy.IncludePrereleases(UpdateChannel.Preview));
    }

    [Theory]
    [InlineData(UpdateChannel.Stable, "stable", false)]
    [InlineData(UpdateChannel.Preview, "preview", false)]
    [InlineData(UpdateChannel.Stable, "preview", true)]  // leaving preview may need a "downgrade" to the latest stable
    [InlineData(UpdateChannel.Preview, "stable", true)]
    [InlineData(UpdateChannel.Stable, null, false)]
    public void Downgrade_allowed_only_when_switching_channel(UpdateChannel selected, string? installed, bool expected)
    {
        Assert.Equal(expected, UpdatePolicy.AllowDowngrade(selected, installed));
    }

    [Fact]
    public void Not_installed_when_running_from_bin()
    {
        var reason = UpdatePolicy.NotInstalledReason(velopackReportsInstalled: false, isPortable: false, updateExePath: null, fileExists: _ => true);
        Assert.NotNull(reason);
        Assert.Contains("development build", reason);
    }

    [Fact]
    public void Not_installed_when_update_exe_is_missing()
    {
        var reason = UpdatePolicy.NotInstalledReason(true, false, @"C:\Users\me\AppData\Local\HelmApp\Update.exe", _ => false);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Portable_builds_explain_themselves()
    {
        var reason = UpdatePolicy.NotInstalledReason(true, isPortable: true, @"C:\x\Update.exe", _ => true);
        Assert.Contains("portable", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Installed_when_velopack_and_update_exe_agree()
    {
        Assert.Null(UpdatePolicy.NotInstalledReason(true, false, @"C:\x\Update.exe", path => path.EndsWith("Update.exe")));
    }
}

public class UpdateStateMachineTests
{
    [Fact]
    public void Happy_path_up_to_date_then_available_then_downloaded_then_applying()
    {
        var m = new UpdateStateMachine();
        var seen = new List<UpdateState>();
        m.Changed += (_, s) => seen.Add(s);

        m.Fire(UpdateTrigger.CheckStarted);
        m.Fire(UpdateTrigger.NoUpdate);
        m.Fire(UpdateTrigger.CheckStarted);
        m.Fire(UpdateTrigger.UpdateFound);
        m.Fire(UpdateTrigger.DownloadStarted);
        m.Fire(UpdateTrigger.DownloadCompleted);
        m.Fire(UpdateTrigger.ApplyStarted);

        Assert.Equal(
        [
            UpdateState.Checking, UpdateState.UpToDate, UpdateState.Checking, UpdateState.UpdateAvailable,
            UpdateState.Downloading, UpdateState.Downloaded, UpdateState.Applying,
        ], seen);
    }

    [Theory]
    [InlineData(UpdateTrigger.ApplyStarted)]
    [InlineData(UpdateTrigger.DownloadStarted)]
    [InlineData(UpdateTrigger.DownloadCompleted)]
    [InlineData(UpdateTrigger.NoUpdate)]
    public void Cannot_skip_steps_from_idle(UpdateTrigger trigger)
    {
        var m = new UpdateStateMachine();
        Assert.False(m.TryFire(trigger));
        Assert.Equal(UpdateState.Idle, m.State);
    }

    [Fact]
    public void Cannot_apply_before_download_completes()
    {
        var m = new UpdateStateMachine();
        m.Fire(UpdateTrigger.CheckStarted);
        m.Fire(UpdateTrigger.UpdateFound);
        Assert.False(m.CanFire(UpdateTrigger.ApplyStarted));
        m.Fire(UpdateTrigger.DownloadStarted);
        Assert.False(m.CanFire(UpdateTrigger.ApplyStarted));
    }

    [Fact]
    public void Failures_are_retryable()
    {
        var m = new UpdateStateMachine();
        m.Fire(UpdateTrigger.CheckStarted);
        m.Fire(UpdateTrigger.Failed);
        Assert.Equal(UpdateState.Failed, m.State);
        Assert.True(m.TryFire(UpdateTrigger.CheckStarted));
    }

    [Fact]
    public void Failure_is_only_possible_while_working()
    {
        var m = new UpdateStateMachine();
        Assert.False(m.TryFire(UpdateTrigger.Failed));
        m.Fire(UpdateTrigger.CheckStarted);
        m.Fire(UpdateTrigger.NoUpdate);
        Assert.False(m.TryFire(UpdateTrigger.Failed));
    }

    [Fact]
    public void An_already_downloaded_update_is_recognized_during_a_check()
    {
        var m = new UpdateStateMachine();
        m.Fire(UpdateTrigger.CheckStarted);
        m.Fire(UpdateTrigger.DownloadCompleted);
        Assert.Equal(UpdateState.Downloaded, m.State);
    }

    [Fact]
    public void Not_installed_is_terminal()
    {
        var m = new UpdateStateMachine();
        m.Fire(UpdateTrigger.NotInstalled);
        Assert.Equal(UpdateState.NotInstalled, m.State);
        foreach (var trigger in Enum.GetValues<UpdateTrigger>())
            Assert.False(m.TryFire(trigger));
    }
}
