using Helm.Ai.Assistant;
using Helm.Core.Settings;

namespace Helm.Tests;

/// <summary>
/// The other ways to open Ask Helm on the phone: the card's open extra, the settings' defaults and limits, the edge
/// handle's place and its touch (40dp inward or a tap), when it shows, and the "Ways to open" page.
/// </summary>
public sealed class AskHelmOpeningTests : IDisposable
{
    private const float Density = 3f;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ---- the card's extra ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AskHelmOpenMode.Listen, "listen")]
    [InlineData(AskHelmOpenMode.Type, "type")]
    [InlineData(AskHelmOpenMode.Camera, "camera")]
    public void Open_modes_round_trip_through_the_extra(AskHelmOpenMode mode, string value)
    {
        Assert.Equal(value, AskHelmOpen.Value(mode));
        Assert.Equal(mode, AskHelmOpen.Parse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("video")]
    public void A_missing_or_unknown_extra_listens_as_before(string? value) => Assert.Equal(AskHelmOpenMode.Listen, AskHelmOpen.Parse(value));

    // ---- settings ----------------------------------------------------------------------------------------------

    [Fact]
    public void Defaults_are_the_right_edge_96dp_45_percent_hidden_over_full_screen_with_the_icon()
    {
        var s = new AskHelmOpenSettings();
        Assert.False(s.EdgeHandle);
        Assert.Equal(EdgeSide.Right, s.EdgeSide);
        Assert.Equal(96, s.EdgeHeightDp);
        Assert.Equal(45, s.EdgePositionPercent);
        Assert.True(s.HideOverFullScreen);
        Assert.True(s.LauncherIcon);
    }

    [Theory]
    [InlineData(0, 48)]
    [InlineData(48, 48)]
    [InlineData(51, 48)]
    [InlineData(53, 56)]
    [InlineData(96, 96)]
    [InlineData(160, 160)]
    [InlineData(200, 160)]
    public void Height_stays_within_48_and_160dp_on_8dp_steps(int dp, int expected) => Assert.Equal(expected, AskHelmOpenSettings.ClampHeight(dp));

    [Theory]
    [InlineData(-10, 5)]
    [InlineData(45, 45)]
    [InlineData(100, 95)]
    public void Position_stays_within_5_and_95_percent(int percent, int expected) => Assert.Equal(expected, AskHelmOpenSettings.ClampPosition(percent));

    [Fact]
    public void Settings_survive_a_restart_with_the_side_as_text()
    {
        var paths = new HelmPaths(_dir.Path);
        using (var factory = new SettingsStoreFactory(paths))
        {
            var store = factory.Get<AskHelmOpenSettings>(AskHelmOpenSettings.StoreId);
            store.Update(s => { s.EdgeHandle = true; s.EdgeSide = EdgeSide.Left; s.EdgeHeightDp = 120; });
            store.Flush();
            Assert.Contains("\"left\"", File.ReadAllText(store.FilePath));
        }
        using var again = new SettingsStoreFactory(paths);
        var read = again.Get<AskHelmOpenSettings>(AskHelmOpenSettings.StoreId).Current;
        Assert.True(read.EdgeHandle);
        Assert.Equal(EdgeSide.Left, read.EdgeSide);
        Assert.Equal(120, read.EdgeHeightDp);
        Assert.Equal(45, read.EdgePositionPercent);
    }

    // ---- where the handle goes -------------------------------------------------------------------------------------

    [Fact]
    public void The_handle_sits_at_its_percent_of_the_room_left_below_it()
    {
        // 2400 px tall, 96dp = 288 px: 45% of the 2112 px left.
        var frame = EdgeHandleLayout.Place(2400, Density, 96, 45);
        Assert.Equal(288, frame.Height);
        Assert.Equal(950, frame.Top);
        Assert.Equal(60, frame.Width);
    }

    [Fact]
    public void The_handle_never_leaves_the_screen()
    {
        var top = EdgeHandleLayout.Place(2400, Density, 160, 100);
        Assert.Equal(480, top.Height);
        Assert.Equal((int)Math.Round((2400 - 480) * 0.95), top.Top);
        Assert.True(top.Top + top.Height <= 2400);

        var tiny = EdgeHandleLayout.Place(100, Density, 160, 45);
        Assert.Equal(100, tiny.Height);
        Assert.Equal(0, tiny.Top);
    }

    [Fact]
    public void While_dragging_the_strip_is_wide_enough_for_the_line_to_follow_44dp()
    {
        Assert.Equal(EdgeHandleLayout.Px(EdgeHandleLayout.StripDp, Density), EdgeHandleLayout.StripPx(false, Density));
        // The widest line and its insets on both sides, plus the whole pull.
        var room = EdgeHandleLayout.DragStripDp - (EdgeHandleLayout.LineDp + EdgeHandleLayout.LineGrowDp) - 2 * EdgeHandleLayout.LineInsetDp;
        Assert.Equal(EdgeHandleGesture.MaxPullDp, room);
        Assert.True(EdgeHandleLayout.DragStripDp < 200);
        Assert.Equal(EdgeHandleLayout.Px(EdgeHandleLayout.DragStripDp, Density), EdgeHandleLayout.StripPx(true, Density));
    }

    [Fact]
    public void A_stored_height_past_the_back_gesture_limit_is_drawn_at_160dp()
    {
        // Android keeps at most 200dp of an edge from the Back gesture for an app.
        var frame = EdgeHandleLayout.Place(3000, 1f, 400, 50);
        Assert.Equal(160, frame.Height);
    }

    // ---- the touch ---------------------------------------------------------------------------------------------

    [Fact]
    public void Dragging_40dp_inward_from_the_right_opens_once()
    {
        var g = new EdgeHandleGesture(EdgeSide.Right, Density);
        g.Down(1080, 1000);
        Assert.Equal(EdgeHandleAction.None, g.Move(1080 - 39 * Density, 1000));
        Assert.Equal(EdgeHandleAction.Open, g.Move(1080 - 40 * Density, 1000));
        Assert.Equal(EdgeHandleAction.None, g.Move(1080 - 60 * Density, 1000));
        Assert.Equal(EdgeHandleAction.None, g.Up(1080 - 60 * Density, 1000));
    }

    [Fact]
    public void Dragging_inward_from_the_left_goes_to_the_right()
    {
        var g = new EdgeHandleGesture(EdgeSide.Left, Density);
        g.Down(0, 1000);
        Assert.Equal(EdgeHandleAction.None, g.Move(-50 * Density, 1000));
        Assert.Equal(EdgeHandleAction.Open, g.Move(41 * Density, 1000));
    }

    [Fact]
    public void A_short_drag_or_one_along_the_edge_does_nothing()
    {
        var g = new EdgeHandleGesture(EdgeSide.Right, Density);
        g.Down(1080, 1000);
        g.Move(1080 - 30 * Density, 1000);
        Assert.Equal(EdgeHandleAction.None, g.Up(1080 - 30 * Density, 1000));

        g.Down(1080, 1000);
        g.Move(1080, 1000 + 80 * Density);
        Assert.Equal(EdgeHandleAction.None, g.Up(1080, 1000 + 80 * Density));
    }

    [Fact]
    public void Letting_go_past_40dp_opens_even_without_a_move_in_between()
    {
        var g = new EdgeHandleGesture(EdgeSide.Right, Density);
        g.Down(1080, 1000);
        Assert.Equal(EdgeHandleAction.Open, g.Up(1080 - 45 * Density, 1000));
    }

    [Fact]
    public void A_tap_opens_and_a_little_wobble_is_still_a_tap()
    {
        var g = new EdgeHandleGesture(EdgeSide.Right, Density);
        g.Down(1075, 1000);
        g.Move(1075 - 5 * Density, 1000 + 4 * Density);
        Assert.Equal(EdgeHandleAction.Open, g.Up(1075 - 5 * Density, 1000 + 4 * Density));
    }

    [Fact]
    public void A_cancelled_gesture_does_nothing_and_the_line_comes_back()
    {
        var g = new EdgeHandleGesture(EdgeSide.Right, Density);
        g.Down(1080, 1000);
        g.Move(1080 - 20 * Density, 1000);
        Assert.Equal(20 * Density, g.Pull, 3);
        Assert.Equal(0.5f, g.Progress, 3);
        g.Cancel();
        Assert.Equal(0, g.Pull);
        Assert.Equal(EdgeHandleAction.None, g.Up(1080, 1000));
    }

    [Fact]
    public void The_line_follows_the_finger_44dp_at_most_and_never_outward()
    {
        var g = new EdgeHandleGesture(EdgeSide.Right, Density);
        g.Down(1080, 1000);
        g.Move(1080 - 100 * Density, 1000);
        Assert.Equal(44 * Density, g.Pull, 3);
        Assert.Equal(1f, g.Progress, 3);
        g.Move(1080 + 10 * Density, 1000);
        Assert.Equal(0, g.Pull);
    }

    // ---- when it shows -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void The_handle_needs_its_switch_the_tool_and_the_permission(bool on, bool moduleOn, bool canDraw, bool wanted) =>
        Assert.Equal(wanted, EdgeHandleRules.Wanted(new AskHelmOpenSettings { EdgeHandle = on }, moduleOn, canDraw));

    [Fact]
    public void Over_a_full_screen_app_it_hides_only_when_asked()
    {
        Assert.False(EdgeHandleRules.Shown(new AskHelmOpenSettings { HideOverFullScreen = true }, fullScreen: true));
        Assert.True(EdgeHandleRules.Shown(new AskHelmOpenSettings { HideOverFullScreen = false }, fullScreen: true));
        Assert.True(EdgeHandleRules.Shown(new AskHelmOpenSettings { HideOverFullScreen = true }, fullScreen: false));
    }

    // ---- the page ----------------------------------------------------------------------------------------------

    [Fact]
    public void Turning_the_handle_on_without_the_permission_opens_the_phone_page_and_saves()
    {
        using var factory = new SettingsStoreFactory(new HelmPaths(_dir.Path));
        var platform = new FakePlatform { Draw = false };
        var vm = new AskHelmWaysViewModel(factory, platform);
        Assert.True(vm.NeedsPermission);

        vm.EdgeHandle = true;
        Assert.Equal(1, platform.OpenedSettings);
        Assert.True(factory.Get<AskHelmOpenSettings>(AskHelmOpenSettings.StoreId).Current.EdgeHandle);

        platform.Draw = true;
        vm.Refresh();
        Assert.False(vm.NeedsPermission);
        Assert.EndsWith("Granted.", vm.PermissionText, StringComparison.Ordinal);
        vm.EdgeHandle = false;
        vm.EdgeHandle = true;
        Assert.Equal(1, platform.OpenedSettings);
    }

    [Fact]
    public void Sliders_snap_and_show_their_values()
    {
        using var factory = new SettingsStoreFactory(new HelmPaths(_dir.Path));
        var vm = new AskHelmWaysViewModel(factory, new FakePlatform());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.HeightDp = 131.4;
        Assert.Equal(128, vm.HeightDp);
        Assert.Equal("128dp", vm.HeightText);
        vm.PositionPercent = 99;
        Assert.Equal("95% down", vm.PositionText);
        Assert.Contains(nameof(vm.HeightText), changed);
        Assert.Contains(nameof(vm.PositionText), changed);

        vm.IsLeft = true;
        Assert.False(vm.IsRight);
        Assert.Equal(EdgeSide.Left, factory.Get<AskHelmOpenSettings>(AskHelmOpenSettings.StoreId).Current.EdgeSide);
    }

    [Fact]
    public void The_widget_row_asks_the_launcher_only_where_it_can_and_the_tile_shows_its_state()
    {
        using var factory = new SettingsStoreFactory(new HelmPaths(_dir.Path));
        var platform = new FakePlatform { Pin = false };
        var vm = new AskHelmWaysViewModel(factory, platform);
        Assert.Contains("Long-press", vm.WidgetText, StringComparison.Ordinal);
        vm.AddWidgetCommand.Execute(null);
        Assert.Equal(0, platform.Pinned);
        Assert.StartsWith("Not added", vm.TileText, StringComparison.Ordinal);

        platform.Pin = true;
        platform.Tile = true;
        vm.Refresh();
        vm.AddWidgetCommand.Execute(null);
        Assert.Equal(1, platform.Pinned);
        Assert.Equal("Added", vm.TileText);
    }

    private sealed class FakePlatform : IAskHelmOpenPlatform
    {
        public bool Draw { get; set; } = true;
        public bool Pin { get; set; } = true;
        public bool Tile { get; set; }
        public int OpenedSettings { get; private set; }
        public int Pinned { get; private set; }

        public bool CanDrawOverlays() => Draw;
        public void OpenOverlaySettings() => OpenedSettings++;
        public bool CanPinWidget() => Pin;
        public void PinWidget() => Pinned++;
        public bool TileAdded() => Tile;
    }
}
