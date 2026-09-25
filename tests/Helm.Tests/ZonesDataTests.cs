using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Settings;
using Helm.Modules.Zones;
using Helm.Modules.Zones.Layouts;

namespace Helm.Tests;

public sealed class ZonesDataTests : IDisposable
{
    private static readonly MonitorInfo Left = new(1, "DISPLAY1_1920x1080", @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, true);
    private static readonly MonitorInfo Right = new(2, "DISPLAY2_1920x1080", @"\\.\DISPLAY2", new PixelRect(1920, 0, 3840, 1080), new PixelRect(1920, 0, 3840, 1040), 96, false);
    private static readonly IReadOnlyList<MonitorInfo> Both = [Left, Right];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Custom_layouts_round_trip()
    {
        var path = Path.Combine(_dir, "layouts.json");
        using (var store = new SettingsStore<LayoutsFile>(path))
        {
            store.Update(f => f.Layouts.Add(new ZoneLayout
            {
                Id = "wide",
                Name = "Wide",
                Number = 3,
                Scope = LayoutScope.Span,
                MonitorIds = [Left.Id, Right.Id],
                Zones = [new ZoneRect(0, 0, 0.75, 1), new ZoneRect(0.75, 0, 0.25, 1)],
            }));
            store.Flush();
        }

        using var reloaded = new SettingsStore<LayoutsFile>(path);
        var layout = reloaded.Current.Find("wide")!;
        Assert.Equal(3, layout.Number);
        Assert.Equal(LayoutScope.Span, layout.Scope);
        Assert.Equal(2, layout.Zones.Count);
        Assert.Equal(0.75, layout.Zones[0].Width, 9);
        Assert.DoesNotContain("\"kind\"", File.ReadAllText(path)); // legacy fields are never written
    }

    [Fact]
    public void Version_1_file_keeps_custom_layouts_and_drops_templates()
    {
        var path = Path.Combine(_dir, "layouts.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """
        { "version": 1, "layouts": [
          { "id": "template-columns", "name": "Columns", "kind": "columns", "zoneCount": 3 },
          { "id": "mine", "name": "Main + sidebar", "kind": "customGrid",
            "grid": { "rows": [0.5, 0.5], "columns": [0.7, 0.3], "cells": [[0, 1], [0, 2]] } },
          { "id": "free", "name": "Floating", "kind": "customCanvas",
            "canvas": [ { "x": 0.1, "y": 0.1, "width": 0.5, "height": 0.5 } ] }
        ] }
        """);

        using var store = new SettingsStore<LayoutsFile>(path);

        var layouts = store.Current.Layouts;
        Assert.Equal(["mine", "free"], layouts.Select(l => l.Id));
        Assert.Equal(3, layouts[0].Zones.Count);
        Assert.Equal(1, layouts[0].Number);
        Assert.Equal(2, layouts[1].Number);
        Assert.All(layouts, l => Assert.Equal(LayoutScope.Monitor, l.Scope));
        Assert.All(layouts, l => Assert.Null(l.LegacyKind));
    }

    [Fact]
    public void Fresh_install_gets_an_editable_starter_layout()
    {
        var data = new ZonesDataService(new SettingsStoreFactory(new HelmPaths(_dir)));

        var layout = Assert.Single(data.GetLayouts());
        Assert.Equal(1, layout.Number);
        Assert.Equal(2, layout.Zones.Count);
        Assert.NotNull(data.GetZones(Left, Both));
    }

    [Fact]
    public void Number_shortcut_applies_to_the_monitor_under_the_cursor_only()
    {
        var data = new ZonesDataService(new SettingsStoreFactory(new HelmPaths(_dir)));
        var three = new ZoneLayout { Name = "Three", Number = 2, Zones = TestLayouts.Columns(3).ToList() };
        data.SaveLayout(three);

        Assert.NotNull(data.ApplyNumber(2, Right, Both));

        Assert.Equal(3, data.GetZones(Right, Both)!.Zones.Count);
        Assert.Equal(2, data.GetZones(Left, Both)!.Zones.Count); // left monitor keeps the starter layout
        Assert.Null(data.ApplyNumber(7, Right, Both));           // no layout with that number
    }

    [Fact]
    public void Spanning_layout_covers_every_monitor_and_crosses_the_boundary()
    {
        var data = new ZonesDataService(new SettingsStoreFactory(new HelmPaths(_dir)));
        var span = new ZoneLayout
        {
            Name = "Across",
            Number = 5,
            Scope = LayoutScope.Span,
            MonitorIds = [Left.Id, Right.Id],
            Zones = [new ZoneRect(0, 0, 0.25, 1), new ZoneRect(0.25, 0, 0.5, 1), new ZoneRect(0.75, 0, 0.25, 1)],
        };
        data.SaveLayout(span);
        data.ApplyNumber(5, Left, Both);

        var fromLeft = data.GetZones(Left, Both)!;
        var fromRight = data.GetZones(Right, Both)!;
        Assert.Equal(fromLeft.Key, fromRight.Key);                 // one shared zone set
        Assert.Equal(new PixelRect(0, 0, 3840, 1040), fromLeft.Reference);
        Assert.Equal(new PixelRect(960, 0, 2880, 1040), fromLeft.Zones[1]); // the middle zone straddles both monitors
    }

    [Fact]
    public void Saving_a_number_takes_it_from_the_previous_owner()
    {
        var data = new ZonesDataService(new SettingsStoreFactory(new HelmPaths(_dir)));
        data.SaveLayout(new ZoneLayout { Id = "b", Name = "B", Number = 1, Zones = TestLayouts.Columns(2).ToList() });

        Assert.Single(data.GetLayouts(), l => l.Number == 1);
        Assert.Equal("b", data.GetLayouts().Single(l => l.Number == 1).Id);
    }

    [Fact]
    public void Applied_map_is_case_insensitive_and_cleans_up_deleted_layouts()
    {
        var data = new ZonesDataService(new SettingsStoreFactory(new HelmPaths(_dir)));
        var layout = new ZoneLayout { Id = "x", Name = "X", Zones = TestLayouts.Rows(2).ToList() };
        data.SaveLayout(layout);
        data.Apply(layout, Left, Both);
        Assert.Equal("x", data.GetAppliedLayout(Left with { Id = Left.Id.ToLowerInvariant() })!.Id);

        data.DeleteLayout("x");
        Assert.NotEqual("x", data.GetAppliedLayout(Left)?.Id);
    }
}
