using Helm.Core.Palette;
using Helm.Core.Text;
using Helm.Modules.CommandPalette;

namespace Helm.Tests;

public sealed class PaletteTests
{
    [Theory]
    [InlineData("Ghi chú", "ghi chu")]
    [InlineData("Đường Điện Biên Phủ", "duong dien bien phu")]
    [InlineData("TIẾNG VIỆT", "tieng viet")]
    [InlineData("Café", "cafe")]
    [InlineData("", "")]
    public void Folding_drops_accents_and_case(string text, string folded) => Assert.Equal(folded, TextSearch.Fold(text));

    [Fact]
    public void Scores_prefer_the_start_then_word_starts_then_anywhere_then_the_detail()
    {
        double S(string query, string title, string? detail = null) => TextSearch.Score(TextSearch.Terms(query), title, detail);

        Assert.True(S("not", "Notes") > S("not", "My notes"));
        Assert.True(S("not", "My notes") > S("ote", "Notes"));
        Assert.True(S("ote", "Notes") > S("milk", "Shopping", "buy milk"));
        Assert.True(S("milk", "Shopping", "buy milk") > 0);
        Assert.Equal(0, S("milk bread", "Shopping", "buy milk")); // every word must match
        Assert.True(S("ghi chu", "Ghi chú họp") > 0);
        Assert.True(S("cp", "Command Palette") > 0); // initials
        Assert.Equal(0, S("xyz", "Command Palette"));
        Assert.True(S("notes", "Notes") > S("notes", "Notes settings")); // shorter wins a tie
    }

    [Fact]
    public void Results_are_merged_by_score_and_tools_that_are_off_are_left_out()
    {
        var notes = new FakeProvider("notes", new PaletteItem("Plan", "", PaletteKind.Note, 0.8, () => { }));
        var tracker = new FakeProvider("tracker", new PaletteItem("Plan the trip", "", PaletteKind.Task, 0.9, () => { }));
        var shell = new FakeProvider(null,
            new PaletteItem("Planner app", "", PaletteKind.App, 0.8, () => { }),
            new PaletteItem("Zero", "", PaletteKind.Page, 0, () => { }));
        var broken = new FakeProvider(null) { Throws = true };

        var results = PaletteSearch.Search([notes, tracker, shell, broken], id => id != "tracker", new PaletteQuery("plan"));

        // Tracker is off; the zero score and the broken provider are dropped; a note beats an app at the same score.
        Assert.Equal(["Plan", "Planner app"], results.Select(r => r.Title));
        Assert.Single(PaletteSearch.Search([notes, tracker], _ => true, new PaletteQuery("plan"), max: 1));
    }

    [Fact]
    public void Start_menu_apps_skip_uninstallers_and_duplicates()
    {
        using var common = new TempDir();
        using var user = new TempDir();
        Directory.CreateDirectory(Path.Combine(common.Path, "Tools"));
        File.WriteAllText(Path.Combine(common.Path, "Unity Hub.lnk"), "");
        File.WriteAllText(Path.Combine(common.Path, "Tools", "Rider.lnk"), "");
        File.WriteAllText(Path.Combine(common.Path, "Uninstall Rider.lnk"), "");
        File.WriteAllText(Path.Combine(common.Path, "readme.txt"), "");
        File.WriteAllText(Path.Combine(user.Path, "unity hub.lnk"), "");
        File.WriteAllText(Path.Combine(user.Path, "Docs.url"), "");

        var apps = StartMenuApps.Read([common.Path, user.Path, Path.Combine(user.Path, "missing")]);

        Assert.Equal(["Docs", "Rider", "Unity Hub"], apps.Select(a => a.Name));
        Assert.StartsWith(common.Path, apps.Single(a => a.Name == "Unity Hub").Path);
    }

    [Fact]
    public void The_palette_box_moves_through_results_and_runs_the_chosen_one()
    {
        var ran = new List<string>();
        var items = new[] { "one", "two", "three" }
            .Select((t, i) => new PaletteItem(t, "", PaletteKind.Action, 1 - i * 0.1, () => ran.Add(t))).ToList();
        var vm = new PaletteViewModel(q => q.IsEmpty ? [] : items);
        vm.Chosen += (_, item) => item.Run();

        vm.Query = "x";
        Assert.Equal("one", vm.Selected?.Title);
        vm.Move(1);
        vm.Move(5); // stops at the last one
        Assert.Equal("three", vm.Selected?.Title);
        vm.Move(-1);
        vm.Choose();
        Assert.Equal(["two"], ran);

        vm.Query = "";
        Assert.False(vm.HasResults);
        Assert.False(vm.HasNoResults); // nothing typed is not "nothing found"
    }

    private sealed class FakeProvider(string? moduleId, params PaletteItem[] items) : IPaletteProvider
    {
        public bool Throws { get; init; }

        public string? ModuleId => moduleId;

        public IEnumerable<PaletteItem> Search(PaletteQuery query) => Throws ? throw new InvalidOperationException("broken") : items;
    }
}
