using Helm.Core.Services;
using Helm.Core.Today;
using Helm.Home;
using Helm.Shell.Home;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Ui.Tests;

/// <summary>Home filled from the tools: cards of turned-on tools, alerts in order, ticks that do not rebuild under the finger.</summary>
public sealed class HomeComposerTests
{
    private readonly Links _links = new();
    private readonly HomeTool _tracker = new("tracker", "Tracker", "Planning", Symbol.Apps);
    private readonly HomeTool _habits = new("habits", "Habits", "Planning", Symbol.Apps);

    [Fact]
    public void Cards_come_only_for_tools_that_are_on()
    {
        _habits.IsEnabled = false;
        var home = new HomeViewModel(new Host(), "v1");

        using var composer = Make(home, [new Source("tracker", 90, "a", "b"), new Source("habits", 50, "c")]);

        Assert.Equal(["Tracker"], home.Today.Select(c => c.Tool.Name));
        Assert.Equal(2, home.Today[0].Items.Count);
    }

    [Fact]
    public void Cards_come_most_urgent_first()
    {
        var home = new HomeViewModel(new Host(), "v1");

        using var composer = Make(home, [new Source("habits", 50, "c"), new Source("tracker", 90, "a")]);

        Assert.Equal(["Tracker", "Habits"], home.Today.Select(c => c.Tool.Name));
    }

    [Fact]
    public void Turning_a_tool_on_adds_its_card()
    {
        _habits.IsEnabled = false;
        var home = new HomeViewModel(new Host(), "v1");
        using var composer = Make(home, [new Source("habits", 50, "c")]);
        Assert.Empty(home.Today);

        _habits.IsEnabled = true;

        Assert.Single(home.Today);
    }

    [Fact]
    public void Alerts_are_ordered_and_act_on_the_right_one()
    {
        var second = new Alerts(new HomeAlertData("b", "Second", "", "Go", 30));
        var first = new Alerts(new HomeAlertData("a", "First", "", "Go", 10));
        var home = new HomeViewModel(new Host(), "v1");
        using var composer = new HomeComposer(home, [_tracker, _habits], [], [second, first], _links, new Inline());

        Assert.Equal(["First", "Second"], home.Alerts.Select(a => a.Title));
        home.Alerts[1].ActCommand.Execute(null);
        Assert.Equal("b", second.Acted);
    }

    [Fact]
    public void Ticking_does_not_rebuild_the_card_but_a_later_change_does()
    {
        var tracker = new Source("tracker", 90, "a", "b");
        var home = new HomeViewModel(new Host(), "v1");
        using var composer = Make(home, [tracker]);
        var card = home.Today[0];

        card.Items[0].TickCommand.Execute(null);

        Assert.Equal(("a", true), tracker.LastTick);
        Assert.Same(card, home.Today[0]);
        Assert.Equal("1 thing needs you today", home.Headline);

        tracker.Raise();
        Assert.NotSame(card, home.Today[0]);
    }

    [Fact]
    public void A_card_button_opens_its_tool()
    {
        var home = new HomeViewModel(new Host(), "v1");
        using var composer = Make(home, [new Source("tracker", 90, "a")]);

        home.Today[0].ActCommand.Execute(null);

        Assert.Equal("tracker", _links.Opened);
    }

    private HomeComposer Make(HomeViewModel home, IEnumerable<ITodaySource> sources) =>
        new(home, [_tracker, _habits], sources, [], _links, new Inline());

    private sealed class Source(string moduleId, int urgency, params string[] items) : ITodaySource
    {
        public string ModuleId => moduleId;

        public (string, bool)? LastTick { get; private set; }

        public event EventHandler? Changed;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public TodayList? Build() =>
            new(moduleId, $"{items.Length} open", [.. items.Select(i => new TodayEntry(i, i, CanTick: true))], "Open", urgency);

        public void Tick(string entryId, bool done)
        {
            LastTick = (entryId, done);
            Raise();
        }
    }

    private sealed class Alerts(params HomeAlertData[] data) : IHomeAlertSource
    {
        public string? Acted { get; private set; }

        public event EventHandler? Changed { add { } remove { } }

        public IReadOnlyList<HomeAlertData> Current() => data;

        public Task Act(string alertId)
        {
            Acted = alertId;
            return Task.CompletedTask;
        }
    }

    private sealed class Links : IHomeLinks
    {
        public string? Opened { get; private set; }

        public void OpenTool(string moduleId) => Opened = moduleId;

        public void OpenGeneral(string part) { }

        public void OpenWhatsNew() { }
    }

    private sealed class Host : IHomeHost
    {
        public Task<string> CaptureAsync(string text) => Task.FromResult("");

        public void OpenWhatsNew() { }
    }

    private sealed class Inline : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
