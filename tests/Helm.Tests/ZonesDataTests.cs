using Helm.Core.Settings;
using Helm.Modules.Zones.Layouts;

namespace Helm.Tests;

public sealed class ZonesDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Layouts_round_trip_with_grid_and_canvas()
    {
        var path = Path.Combine(_dir, "layouts.json");
        using (var store = new SettingsStore<LayoutsFile>(path))
        {
            store.Update(f =>
            {
                f.EnsureDefaults();
                f.Layouts.Add(new LayoutDefinition
                {
                    Id = "mine",
                    Name = "Mine",
                    Kind = LayoutKind.CustomGrid,
                    Grid = GridLayout.Single().Split(0, SplitOrientation.Vertical, 0.3),
                });
            });
            store.Flush();
        }

        using var reloaded = new SettingsStore<LayoutsFile>(path);
        var mine = reloaded.Current.Find("mine");
        Assert.NotNull(mine);
        Assert.Equal(2, mine!.GetZones().Count);
        Assert.Equal(0.3, mine.GetZones()[0].Width, 9);

        var canvas = reloaded.Current.Find("sample-canvas");
        Assert.NotNull(canvas);
        Assert.Equal(3, canvas!.GetZones().Count);
        Assert.False(canvas.UsesSpacing);
    }

    [Fact]
    public void Applied_map_round_trips_per_monitor()
    {
        var path = Path.Combine(_dir, "applied.json");
        using (var store = new SettingsStore<AppliedFile>(path))
        {
            store.Update(f => f.Monitors["DISPLAY1_2560x1440"] = new AppliedLayout { LayoutId = "template-grid", Spacing = 8 });
            store.Flush();
        }

        using var reloaded = new SettingsStore<AppliedFile>(path);
        var applied = reloaded.Current.Monitors["display1_2560x1440"]; // ids are case-insensitive
        Assert.Equal("template-grid", applied.LayoutId);
        Assert.Equal(8, applied.Spacing);
    }
}
